using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class CommunicationPoolManager : ICommunicationPoolManager
{
    private const string CrdGroup = "octo-mesh.meshmakers.io";
    private const string CrdVersion = "v1alpha1";
    private const string CrdPlural = "communicationpools";

    private readonly ILogger<CommunicationPoolManager> _logger;
    private readonly OperatorOptions _options;
    private readonly IKubernetes _kubernetesClient;

    public CommunicationPoolManager(
        ILogger<CommunicationPoolManager> logger,
        IOptions<OperatorOptions> options,
        IKubernetes kubernetesClient)
    {
        _logger = logger;
        _options = options.Value;
        _kubernetesClient = kubernetesClient;
    }

    public async Task CreateCommunicationPoolAsync(string tenantId)
    {
        var crName = GetCrName(tenantId);
        var ns = _options.PoolNamespace;

        if (await CommunicationPoolExistsAsync(crName, ns))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' already exists in namespace '{Namespace}', skipping creation",
                crName, ns);
            return;
        }

        await CreateBrokerSecretAsync(tenantId, ns);

        var resource = new CommunicationPoolResource
        {
            Metadata = new CommunicationPoolMetadata
            {
                Name = crName,
                Namespace = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = tenantId,
                    ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator"
                }
            },
            Spec = new CommunicationPoolSpec
            {
                TenantId = tenantId,
                PoolName = _options.DefaultPoolName,
                CommunicationControllerUri = _options.CommunicationControllerUri,
                InstancePrefix = _options.InstancePrefix ?? string.Empty,
                IgnoreCertificateValidation = _options.AdapterIgnoreCertificateValidation,
                BrokerHost = _options.BrokerHost,
                BrokerVirtualHost = _options.BrokerVirtualHost,
                BrokerPort = _options.BrokerPort
            }
        };

        _logger.LogInformation(
            "Creating CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}'",
            crName, ns, tenantId);

        await _kubernetesClient.CustomObjects.CreateNamespacedCustomObjectAsync(
            resource, CrdGroup, CrdVersion, ns, CrdPlural);

        _logger.LogInformation("CommunicationPool CR '{CrName}' created successfully", crName);
    }

    public async Task DeleteCommunicationPoolAsync(string tenantId)
    {
        var crName = GetCrName(tenantId);
        var ns = _options.PoolNamespace;

        if (!await CommunicationPoolExistsAsync(crName, ns))
        {
            _logger.LogInformation(
                "CommunicationPool CR '{CrName}' does not exist in namespace '{Namespace}', skipping deletion",
                crName, ns);
            return;
        }

        _logger.LogInformation(
            "Deleting CommunicationPool CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}'",
            crName, ns, tenantId);

        await _kubernetesClient.CustomObjects.DeleteNamespacedCustomObjectAsync(
            CrdGroup, CrdVersion, ns, CrdPlural, crName);

        await DeleteBrokerSecretAsync(tenantId, ns);

        _logger.LogInformation("CommunicationPool CR '{CrName}' deleted successfully", crName);
    }

    private async Task<bool> CommunicationPoolExistsAsync(string crName, string ns)
    {
        try
        {
            await _kubernetesClient.CustomObjects.GetNamespacedCustomObjectAsync(
                CrdGroup, CrdVersion, ns, CrdPlural, crName);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task CreateBrokerSecretAsync(string tenantId, string ns)
    {
        var secretName = GetSecretName(tenantId);

        try
        {
            await _kubernetesClient.CoreV1.ReadNamespacedSecretAsync(secretName, ns);
            _logger.LogInformation("Broker secret '{SecretName}' already exists, skipping creation", secretName);
            return;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Secret doesn't exist, create it
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = tenantId,
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

        _logger.LogInformation("Creating broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
        await _kubernetesClient.CoreV1.CreateNamespacedSecretAsync(secret, ns);
    }

    private async Task DeleteBrokerSecretAsync(string tenantId, string ns)
    {
        var secretName = GetSecretName(tenantId);

        try
        {
            _logger.LogInformation("Deleting broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
            await _kubernetesClient.CoreV1.DeleteNamespacedSecretAsync(secretName, ns);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Broker secret '{SecretName}' does not exist, skipping deletion", secretName);
        }
    }

    private string GetCrName(string tenantId)
    {
        return $"{tenantId}-{_options.DefaultPoolName}".ToLowerInvariant();
    }

    private string GetSecretName(string tenantId)
    {
        return $"{tenantId}-{_options.DefaultPoolName}-octo-mesh-connection".ToLowerInvariant();
    }
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
