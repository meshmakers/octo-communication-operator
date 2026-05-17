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

        // 0. If the workload opts in, append secret-flagged overrides for
        //    the cluster-internal credentials the operator knows about.
        //    The resulting overrides flow through the normal secret-flagged
        //    path: materialized into {release}-octo-secrets, referenced from
        //    the chart via valueFrom secretKeyRef.
        workload = workload with { Values = AppendClusterSecrets(workload.Values, workload.ReceivesClusterSecrets, _options) };

        // 1. Materialize / refresh the operator-owned secret. We replace it
        //    every deploy so a value rotation propagates without manual
        //    intervention. When no secret-flagged overrides exist, ensure any
        //    leftover secret from a previous deploy is removed.
        await ReconcileSecretAsync(ns, secretName, workload, cancellationToken);

        // 2. Make sure the chart repository is registered + index refreshed.
        var alias = RepoAlias(workload.RepositoryUrl);
        await _helm.EnsureRepoAsync(alias, workload.RepositoryUrl,
            workload.RepositoryUsername, workload.RepositoryPassword, cancellationToken);

        // 3. Assemble values files. Helm later args win — so order is:
        //    context (operator-managed cluster defaults) → workload
        //    ValuesYaml → structured overrides. Workload-side input always
        //    has the final say.
        var tempDir = Directory.CreateTempSubdirectory("octo-helm-").FullName;
        try
        {
            var valuesFiles = new List<string>();

            var contextYaml = WorkloadContextValuesBuilder.Build(_options, workload);
            if (!string.IsNullOrEmpty(contextYaml))
            {
                var contextFile = Path.Combine(tempDir, "values-context.yaml");
                await File.WriteAllTextAsync(contextFile, contextYaml, cancellationToken);
                valuesFiles.Add(contextFile);
            }

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
                    ["octo-mesh.meshmakers.io/tenant"] = SanitizeLabelValue(workload.TenantId),
                    ["octo-mesh.meshmakers.io/pool"] = SanitizeLabelValue(workload.PoolName),
                    ["octo-mesh.meshmakers.io/workload"] = SanitizeLabelValue(workload.WorkloadName),
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
    /// Returns the workload's existing overrides plus any cluster-credential
    /// overrides the operator can supply, when the workload opted in. Each
    /// injected entry is marked <c>IsSecret = true</c> so it flows through
    /// the per-release Kubernetes Secret rather than appearing as a plain
    /// value in the rendered manifest. Entries the operator does not have
    /// a value for are skipped silently.
    /// </summary>
    internal static IReadOnlyList<ValueOverrideDto> AppendClusterSecrets(
        IReadOnlyList<ValueOverrideDto> existing, bool receivesClusterSecrets, OperatorOptions options)
    {
        var injected = new List<ValueOverrideDto>(4);

        // The RabbitMQ broker password is part of the basic controller↔adapter
        // contract — every adapter needs the command bus, regardless of whether
        // it also touches data stores. Inject it whenever the operator has a
        // BrokerPassword, independent of the ReceivesClusterSecrets opt-in.
        // Previously this was lumped into the cluster-secrets gate, which made
        // pure edge adapters (e.g. Modbus / Loxone) fail the chart's
        // `secrets.rabbitmq must be set` validation unless the user enabled a
        // flag whose name implies cluster-integration semantics it doesn't
        // actually need.
        if (!string.IsNullOrEmpty(options.BrokerPassword))
        {
            injected.Add(new ValueOverrideDto { Path = "secrets.rabbitmq", Value = options.BrokerPassword, IsSecret = true });
        }

        // Data-store credentials (Mongo / CrateDB) only matter for adapters
        // that actually open those connections. Gate on the explicit opt-in
        // so a Mongo-less Modbus pod doesn't carry Mongo creds in its Secret.
        if (receivesClusterSecrets)
        {
            if (!string.IsNullOrEmpty(options.ClusterSecrets.MongodbUserPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.databaseUser", Value = options.ClusterSecrets.MongodbUserPassword, IsSecret = true });
            }
            if (!string.IsNullOrEmpty(options.ClusterSecrets.MongodbAdminPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.databaseAdmin", Value = options.ClusterSecrets.MongodbAdminPassword, IsSecret = true });
            }
            if (!string.IsNullOrEmpty(options.ClusterSecrets.StreamDataPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.streamDataPassword", Value = options.ClusterSecrets.StreamDataPassword, IsSecret = true });
            }
        }

        if (injected.Count == 0)
        {
            return existing;
        }

        // Workload-supplied overrides win — operator-injected entries are
        // appended first so the same path coming from the entity overrides
        // the operator's value. WorkloadOverrideYamlBuilder.SetNested keeps
        // only the last value per path.
        var merged = new List<ValueOverrideDto>(existing.Count + injected.Count);
        merged.AddRange(injected);
        merged.AddRange(existing);
        return merged;
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
    /// Coerces an arbitrary string into a valid Kubernetes label value.
    /// Kubernetes requires labels to match
    /// <c>[A-Za-z0-9][-A-Za-z0-9_.]*[A-Za-z0-9]</c> with length ≤ 63 — workload
    /// names from the CK entity can contain spaces or other punctuation
    /// (e.g. "meshtest Adapter"), which the apiserver rejects with a 422.
    /// Everything outside the allowed alphabet becomes a dash; leading and
    /// trailing dashes are trimmed; empty results become "unknown" so the
    /// label is still set (omitting it entirely would lose the workload
    /// identity in the labels API).
    /// </summary>
    internal static string SanitizeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        var trimmed = sb.ToString().Trim('-', '_', '.');
        if (trimmed.Length == 0)
        {
            return "unknown";
        }
        return trimmed.Length > 63 ? trimmed[..63].TrimEnd('-', '_', '.') : trimmed;
    }

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
