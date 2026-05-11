using System.Text;
using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Drives the per-workload Helm lifecycle. Owns three side effects on each
/// deploy: the operator-managed Kubernetes <c>Secret</c> for secret-flagged
/// overrides, the Helm chart repository registration, and the
/// <c>helm upgrade --install</c> call itself.
/// </summary>
public sealed class WorkloadReconciler : IWorkloadReconciler
{
    private readonly IHelmRunner _helm;
    private readonly ICommunicationPoolKubernetesGateway _gateway;
    private readonly OperatorOptions _options;
    private readonly ILogger<WorkloadReconciler> _logger;

    public WorkloadReconciler(IHelmRunner helm, ICommunicationPoolKubernetesGateway gateway,
        IOptions<OperatorOptions> options, ILogger<WorkloadReconciler> logger)
    {
        _helm = helm;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    public async Task DeployAsync(WorkloadDeployedDto workload, CancellationToken cancellationToken)
    {
        var release = ReleaseName(workload.TenantId, workload.WorkloadName);
        var ns = _options.PoolNamespace;
        var secretName = SecretName(release);

        _logger.LogInformation(
            "Deploying workload: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', chart '{ChartName}:{ChartVersion}', release '{Release}' in namespace '{Namespace}'",
            workload.TenantId, workload.PoolName, workload.WorkloadName,
            workload.ChartName, workload.ChartVersion, release, ns);

        // 1. Materialize / refresh the operator-owned secret. We replace it
        //    every deploy so a value rotation propagates without manual
        //    intervention. When no secret-flagged overrides exist, ensure any
        //    leftover secret from a previous deploy is removed.
        await ReconcileSecretAsync(ns, secretName, workload, cancellationToken);

        // 2. Make sure the chart repository is registered + index refreshed.
        var alias = RepoAlias(workload.RepositoryUrl);
        await _helm.EnsureRepoAsync(alias, workload.RepositoryUrl,
            workload.RepositoryUsername, workload.RepositoryPassword, cancellationToken);

        // 3. Assemble values files. The workload's base ValuesYaml is the
        //    first layer; structured overrides are written to a second file
        //    and override the first.
        var tempDir = Directory.CreateTempSubdirectory("octo-helm-").FullName;
        try
        {
            var valuesFiles = new List<string>();

            if (!string.IsNullOrWhiteSpace(workload.ValuesYaml))
            {
                var baseFile = Path.Combine(tempDir, "values-base.yaml");
                await File.WriteAllTextAsync(baseFile, workload.ValuesYaml, cancellationToken);
                valuesFiles.Add(baseFile);
            }

            var overrideYaml = WorkloadOverrideYamlBuilder.Build(workload.Values, secretName);
            if (!string.IsNullOrEmpty(overrideYaml))
            {
                var overrideFile = Path.Combine(tempDir, "values-overrides.yaml");
                await File.WriteAllTextAsync(overrideFile, overrideYaml, cancellationToken);
                valuesFiles.Add(overrideFile);
            }

            await _helm.UpgradeInstallAsync(release, $"{alias}/{workload.ChartName}",
                workload.ChartVersion, ns, valuesFiles,
                new Dictionary<string, string>(), cancellationToken);
        }
        finally
        {
            // Best-effort cleanup; if it fails it's not fatal — the values
            // contain decrypted secrets, but the directory is in the
            // operator's per-container tmp.
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove temp values directory '{Path}'", tempDir);
            }
        }
    }

    public async Task UndeployAsync(WorkloadUndeployedDto workload, CancellationToken cancellationToken)
    {
        var release = ReleaseName(workload.TenantId, workload.WorkloadName);
        var ns = _options.PoolNamespace;
        var secretName = SecretName(release);

        _logger.LogInformation(
            "Undeploying workload: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', release '{Release}'",
            workload.TenantId, workload.PoolName, workload.WorkloadName, release);

        await _helm.UninstallAsync(release, ns, cancellationToken);

        if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
        {
            await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
        }
    }

    private async Task ReconcileSecretAsync(string ns, string secretName, WorkloadDeployedDto workload,
        CancellationToken cancellationToken)
    {
        var secretEntries = workload.Values.Where(v => v.IsSecret).ToArray();

        if (secretEntries.Length == 0)
        {
            if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
            {
                _logger.LogInformation("No secret values for release '{Secret}'; removing stale secret", secretName);
                await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
            }
            return;
        }

        // Always replace: simplest path that handles add / update / remove
        // of individual keys without per-key diffing.
        if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
        {
            await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
        }

        var data = new Dictionary<string, byte[]>(secretEntries.Length);
        foreach (var entry in secretEntries)
        {
            data[entry.Path] = Encoding.UTF8.GetBytes(entry.Value ?? string.Empty);
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = workload.TenantId,
                    ["octo-mesh.meshmakers.io/pool"] = workload.PoolName,
                    ["octo-mesh.meshmakers.io/workload"] = workload.WorkloadName,
                    ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator",
                },
            },
            Type = "Opaque",
            Data = data,
        };

        _logger.LogInformation("Creating secret '{Secret}' in namespace '{Namespace}' with {Count} entries",
            secretName, ns, data.Count);
        await _gateway.CreateSecretAsync(ns, secret, cancellationToken);
    }

    /// <summary>
    /// Helm release name. DNS-safe: lowercase, alphanumeric + '-'. Truncated
    /// to 53 chars (helm release name limit).
    /// </summary>
    internal static string ReleaseName(string tenantId, string workloadName)
    {
        var raw = ($"{tenantId}-{workloadName}").ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        }
        var trimmed = sb.ToString().Trim('-');
        return trimmed.Length > 53 ? trimmed[..53] : trimmed;
    }

    internal static string SecretName(string release) => $"{release}-octo-secrets";

    /// <summary>
    /// Stable, DNS-safe alias derived from the repository URL. Same URL
    /// produces the same alias every time so repeated <c>helm repo add</c>
    /// calls are idempotent.
    /// </summary>
    internal static string RepoAlias(string repositoryUrl)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(repositoryUrl));
        var hex = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        return $"octo-{hex}";
    }
}
