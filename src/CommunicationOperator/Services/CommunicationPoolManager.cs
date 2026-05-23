using System.Text.Json.Serialization;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Common;
using Meshmakers.Octo.Communication.Operator.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class CommunicationPoolManager : ICommunicationPoolManager
{
    private readonly ILogger<CommunicationPoolManager> _logger;
    private readonly OperatorOptions _options;
    private readonly ICommunicationPoolKubernetesGateway _gateway;

    public CommunicationPoolManager(
        ILogger<CommunicationPoolManager> logger,
        IOptions<OperatorOptions> options,
        ICommunicationPoolKubernetesGateway gateway)
    {
        _logger = logger;
        _options = options.Value;
        _gateway = gateway;
    }

    public async Task CreatePoolAsync(string tenantId, string poolRtId, string poolName)
    {
        var crName = GetCrName(tenantId, poolRtId);
        var ns = _options.PoolNamespace;

        if (await _gateway.CommunicationPoolExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' already exists in namespace '{Namespace}', skipping creation",
                crName, ns);
            return;
        }

        await CreateBrokerSecretIfMissingAsync(tenantId, poolRtId, poolName, ns);

        var resource = BuildCommunicationPoolResource(tenantId, poolRtId, poolName, crName, ns);
        _logger.LogInformation(
            "Creating CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', pool '{PoolName}' (rtId {PoolRtId})",
            crName, ns, tenantId, poolName, poolRtId);
        await _gateway.CreateCommunicationPoolAsync(ns, resource);
        _logger.LogInformation("CommunicationPool CR '{CrName}' created successfully", crName);
    }

    public async Task DeletePoolAsync(string tenantId, string poolRtId, string poolName)
    {
        var crName = GetCrName(tenantId, poolRtId);
        var ns = _options.PoolNamespace;

        if (!await _gateway.CommunicationPoolExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' does not exist in namespace '{Namespace}', skipping deletion",
                crName, ns);
            return;
        }

        _logger.LogInformation(
            "Deleting CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', pool '{PoolName}' (rtId {PoolRtId})",
            crName, ns, tenantId, poolName, poolRtId);
        await _gateway.DeleteCommunicationPoolAsync(ns, crName);

        await DeleteBrokerSecretAsync(tenantId, poolRtId, ns);
        _logger.LogInformation("CommunicationPool CR '{CrName}' deleted successfully", crName);
    }

    private async Task CreateBrokerSecretIfMissingAsync(string tenantId, string poolRtId, string poolName, string ns)
    {
        var secretName = GetSecretName(tenantId, poolRtId);
        if (await _gateway.SecretExistsAsync(ns, secretName))
        {
            _logger.LogInformation(
                "Broker secret '{SecretName}' already exists, skipping creation", secretName);
            return;
        }

        var secret = BuildBrokerSecret(secretName, ns, tenantId, poolRtId, poolName);
        _logger.LogInformation(
            "Creating broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
        await _gateway.CreateSecretAsync(ns, secret);
    }

    private async Task DeleteBrokerSecretAsync(string tenantId, string poolRtId, string ns)
    {
        var secretName = GetSecretName(tenantId, poolRtId);
        if (!await _gateway.SecretExistsAsync(ns, secretName))
        {
            _logger.LogInformation(
                "Broker secret '{SecretName}' does not exist, skipping deletion", secretName);
            return;
        }
        _logger.LogInformation(
            "Deleting broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
        await _gateway.DeleteSecretAsync(ns, secretName);
    }

    private CommunicationPoolResource BuildCommunicationPoolResource(
        string tenantId, string poolRtId, string poolName, string crName, string ns) =>
        new()
        {
            Metadata = new CommunicationPoolMetadata
            {
                Name = crName,
                Namespace = ns,
                Labels = BuildLabels(tenantId, poolRtId),
                Annotations = BuildAnnotations(poolName)
            },
            Spec = new CommunicationPoolSpec
            {
                TenantId = tenantId,
                PoolRtId = poolRtId,
                PoolName = poolName
            }
        };

    private V1Secret BuildBrokerSecret(string secretName, string ns, string tenantId, string poolRtId, string poolName) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = BuildLabels(tenantId, poolRtId),
                Annotations = BuildAnnotations(poolName)
            },
            Type = "Opaque",
            StringData = new Dictionary<string, string>
            {
                ["brokerusername"] = _options.BrokerUser ?? string.Empty,
                ["brokerpassword"] = _options.BrokerPassword ?? string.Empty
            }
        };

    // K8s names and label values are derived from the runtime entity ids,
    // not from user-facing names — CK attributes can carry whitespace,
    // uppercase, and other characters the apiserver rejects with a 422
    // (e.g. "Communication Pool"). RtIds are 24-char lowercase hex
    // strings, always valid; the user-facing PoolName rides along as an
    // annotation for UI/debugging.
    private static Dictionary<string, string> BuildLabels(string tenantId, string poolRtId) =>
        new()
        {
            ["octo-mesh.meshmakers.io/tenant"] = K8sNaming.LabelValue(tenantId),
            ["octo-mesh.meshmakers.io/pool-rt-id"] = poolRtId,
            ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator"
        };

    private static Dictionary<string, string> BuildAnnotations(string poolName) =>
        new()
        {
            ["octo-mesh.meshmakers.io/pool-name"] = poolName
        };

    // CR name: {tenant}-{poolRtId} as a strict RFC 1123 subdomain. Keep
    // the 53-char cap from K8sNaming so the secret name (which appends
    // "-octo-mesh-connection") still fits comfortably under the apiserver
    // 253-char ceiling for any reasonable combination of inputs.
    private static string GetCrName(string tenantId, string poolRtId) =>
        K8sNaming.DnsName(K8sNaming.DefaultDnsNameMaxLength, tenantId, poolRtId);

    private static string GetSecretName(string tenantId, string poolRtId) =>
        $"{GetCrName(tenantId, poolRtId)}-octo-mesh-connection";
}

internal class CommunicationPoolResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = "octo-mesh.meshmakers.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "CommunicationPool";

    [JsonPropertyName("metadata")]
    public CommunicationPoolMetadata Metadata { get; set; } = new();

    [JsonPropertyName("spec")]
    public CommunicationPoolSpec Spec { get; set; } = new();
}

internal class CommunicationPoolMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; set; }

    [JsonPropertyName("annotations")]
    public Dictionary<string, string>? Annotations { get; set; }
}

internal class CommunicationPoolSpec
{
    // Matches V1CommunicationPoolEntitySpec — CR only carries the pool's
    // tenant + rtId + display name. Everything else (controller URI,
    // broker host, instancePrefix, …) is owned by the operator instance
    // that services this CR and read from OperatorOptions at startup.
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("poolRtId")]
    public string PoolRtId { get; set; } = string.Empty;

    [JsonPropertyName("poolName")]
    public string PoolName { get; set; } = string.Empty;
}
