using System.Text.Json.Serialization;
using k8s.Models;
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

    public async Task CreatePoolAsync(string tenantId, string poolName)
    {
        var crName = GetCrName(tenantId, poolName);
        var ns = _options.PoolNamespace;

        if (await _gateway.CommunicationPoolExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' already exists in namespace '{Namespace}', skipping creation",
                crName, ns);
            return;
        }

        await CreateBrokerSecretIfMissingAsync(tenantId, poolName, ns);

        var resource = BuildCommunicationPoolResource(tenantId, poolName, crName, ns);
        _logger.LogInformation(
            "Creating CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', pool '{PoolName}'",
            crName, ns, tenantId, poolName);
        await _gateway.CreateCommunicationPoolAsync(ns, resource);
        _logger.LogInformation("CommunicationPool CR '{CrName}' created successfully", crName);
    }

    public async Task DeletePoolAsync(string tenantId, string poolName)
    {
        var crName = GetCrName(tenantId, poolName);
        var ns = _options.PoolNamespace;

        if (!await _gateway.CommunicationPoolExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' does not exist in namespace '{Namespace}', skipping deletion",
                crName, ns);
            return;
        }

        _logger.LogInformation(
            "Deleting CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', pool '{PoolName}'",
            crName, ns, tenantId, poolName);
        await _gateway.DeleteCommunicationPoolAsync(ns, crName);

        await DeleteBrokerSecretAsync(tenantId, poolName, ns);
        _logger.LogInformation("CommunicationPool CR '{CrName}' deleted successfully", crName);
    }

    private async Task CreateBrokerSecretIfMissingAsync(string tenantId, string poolName, string ns)
    {
        var secretName = GetSecretName(tenantId, poolName);
        if (await _gateway.SecretExistsAsync(ns, secretName))
        {
            _logger.LogInformation(
                "Broker secret '{SecretName}' already exists, skipping creation", secretName);
            return;
        }

        var secret = BuildBrokerSecret(secretName, ns, tenantId, poolName);
        _logger.LogInformation(
            "Creating broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
        await _gateway.CreateSecretAsync(ns, secret);
    }

    private async Task DeleteBrokerSecretAsync(string tenantId, string poolName, string ns)
    {
        var secretName = GetSecretName(tenantId, poolName);
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
        string tenantId, string poolName, string crName, string ns) =>
        new()
        {
            Metadata = new CommunicationPoolMetadata
            {
                Name = crName,
                Namespace = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = tenantId,
                    ["octo-mesh.meshmakers.io/pool"] = poolName,
                    ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator"
                }
            },
            Spec = new CommunicationPoolSpec
            {
                TenantId = tenantId,
                PoolName = poolName,
                CommunicationControllerUri = _options.CommunicationControllerUri,
                InstancePrefix = _options.InstancePrefix ?? string.Empty,
                IgnoreCertificateValidation = _options.AdapterIgnoreCertificateValidation,
                BrokerHost = _options.BrokerHost,
                BrokerVirtualHost = _options.BrokerVirtualHost,
                BrokerPort = _options.BrokerPort
            }
        };

    private V1Secret BuildBrokerSecret(string secretName, string ns, string tenantId, string poolName) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = tenantId,
                    ["octo-mesh.meshmakers.io/pool"] = poolName,
                    ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator"
                }
            },
            Type = "Opaque",
            StringData = new Dictionary<string, string>
            {
                ["brokerusername"] = _options.BrokerUser ?? string.Empty,
                ["brokerpassword"] = _options.BrokerPassword ?? string.Empty
            }
        };

    private static string GetCrName(string tenantId, string poolName) =>
        $"{tenantId}-{poolName}".ToLowerInvariant();

    private static string GetSecretName(string tenantId, string poolName) =>
        $"{tenantId}-{poolName}-octo-mesh-connection".ToLowerInvariant();
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
}

internal class CommunicationPoolSpec
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("poolName")]
    public string PoolName { get; set; } = string.Empty;

    [JsonPropertyName("communicationControllerUri")]
    public string CommunicationControllerUri { get; set; } = string.Empty;

    [JsonPropertyName("instancePrefix")]
    public string InstancePrefix { get; set; } = string.Empty;

    [JsonPropertyName("ignoreCertificateValidation")]
    public bool IgnoreCertificateValidation { get; set; }

    [JsonPropertyName("brokerHost")]
    public string BrokerHost { get; set; } = string.Empty;

    [JsonPropertyName("brokerVirtualHost")]
    public string BrokerVirtualHost { get; set; } = string.Empty;

    [JsonPropertyName("brokerPort")]
    public int BrokerPort { get; set; } = 5672;
}
