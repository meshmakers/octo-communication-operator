using System.Text.Json.Serialization;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Common;
using Meshmakers.Octo.Communication.Operator.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class DeploymentSiteManager : IDeploymentSiteManager
{
    private readonly ILogger<DeploymentSiteManager> _logger;
    private readonly OperatorOptions _options;
    private readonly IDeploymentSiteKubernetesGateway _gateway;

    public DeploymentSiteManager(
        ILogger<DeploymentSiteManager> logger,
        IOptions<OperatorOptions> options,
        IDeploymentSiteKubernetesGateway gateway)
    {
        _logger = logger;
        _options = options.Value;
        _gateway = gateway;
    }

    public async Task CreateDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        var crName = GetCrName(tenantId, deploymentSiteRtId);
        var ns = _options.DeploymentSiteNamespace;

        if (await _gateway.DeploymentSiteExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "DeploymentSite CR '{CrName}' already exists in namespace '{Namespace}', skipping creation",
                crName, ns);
            return;
        }

        await CreateBrokerSecretIfMissingAsync(tenantId, deploymentSiteRtId, ns);

        var resource = BuildDeploymentSiteResource(tenantId, deploymentSiteRtId, crName, ns);
        _logger.LogInformation(
            "Creating DeploymentSite CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            crName, ns, tenantId, deploymentSiteRtId);
        await _gateway.CreateDeploymentSiteAsync(ns, resource);
        _logger.LogInformation("DeploymentSite CR '{CrName}' created successfully", crName);
    }

    public async Task DeleteDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        var crName = GetCrName(tenantId, deploymentSiteRtId);
        var ns = _options.DeploymentSiteNamespace;

        if (!await _gateway.DeploymentSiteExistsAsync(ns, crName))
        {
            _logger.LogInformation(
                "DeploymentSite CR '{CrName}' does not exist in namespace '{Namespace}', skipping deletion",
                crName, ns);
            return;
        }

        _logger.LogInformation(
            "Deleting DeploymentSite CR '{CrName}' in namespace '{Namespace}' for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            crName, ns, tenantId, deploymentSiteRtId);
        await _gateway.DeleteDeploymentSiteAsync(ns, crName);

        await DeleteBrokerSecretAsync(tenantId, deploymentSiteRtId, ns);
        _logger.LogInformation("DeploymentSite CR '{CrName}' deleted successfully", crName);
    }

    private async Task CreateBrokerSecretIfMissingAsync(string tenantId, string deploymentSiteRtId, string ns)
    {
        var secretName = GetSecretName(tenantId, deploymentSiteRtId);
        if (await _gateway.SecretExistsAsync(ns, secretName))
        {
            _logger.LogInformation(
                "Broker secret '{SecretName}' already exists, skipping creation", secretName);
            return;
        }

        var secret = BuildBrokerSecret(secretName, ns, tenantId, deploymentSiteRtId);
        _logger.LogInformation(
            "Creating broker secret '{SecretName}' in namespace '{Namespace}'", secretName, ns);
        await _gateway.CreateSecretAsync(ns, secret);
    }

    private async Task DeleteBrokerSecretAsync(string tenantId, string deploymentSiteRtId, string ns)
    {
        var secretName = GetSecretName(tenantId, deploymentSiteRtId);
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

    private DeploymentSiteResource BuildDeploymentSiteResource(
        string tenantId, string deploymentSiteRtId, string crName, string ns) =>
        new()
        {
            Metadata = new DeploymentSiteMetadata
            {
                Name = crName,
                Namespace = ns,
                Labels = BuildLabels(tenantId, deploymentSiteRtId)
            },
            Spec = new DeploymentSiteSpec
            {
                TenantId = tenantId,
                DeploymentSiteRtId = deploymentSiteRtId
            }
        };

    private V1Secret BuildBrokerSecret(string secretName, string ns, string tenantId, string deploymentSiteRtId) =>
        new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = BuildLabels(tenantId, deploymentSiteRtId)
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
    // (e.g. "Communication DeploymentSite"). RtIds are 24-char lowercase hex
    // strings, always valid.
    private static Dictionary<string, string> BuildLabels(string tenantId, string deploymentSiteRtId) =>
        new()
        {
            ["octo-mesh.meshmakers.io/tenant"] = K8sNaming.LabelValue(tenantId),
            ["octo-mesh.meshmakers.io/deployment-site-rt-id"] = deploymentSiteRtId,
            ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator"
        };

    // CR name: {tenant}-{deploymentSiteRtId} as a strict RFC 1123 subdomain. Keep
    // the 53-char cap from K8sNaming so the secret name (which appends
    // "-octo-mesh-connection") still fits comfortably under the apiserver
    // 253-char ceiling for any reasonable combination of inputs.
    internal static string GetCrName(string tenantId, string deploymentSiteRtId) =>
        K8sNaming.DnsName(K8sNaming.DefaultDnsNameMaxLength, tenantId, deploymentSiteRtId);

    private static string GetSecretName(string tenantId, string deploymentSiteRtId) =>
        $"{GetCrName(tenantId, deploymentSiteRtId)}-octo-mesh-connection";
}

internal class DeploymentSiteResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = "octo-mesh.meshmakers.io/v1";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "DeploymentSite";

    [JsonPropertyName("metadata")]
    public DeploymentSiteMetadata Metadata { get; set; } = new();

    [JsonPropertyName("spec")]
    public DeploymentSiteSpec Spec { get; set; } = new();
}

internal class DeploymentSiteMetadata
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

internal class DeploymentSiteSpec
{
    // Matches V1DeploymentSiteEntitySpec — CR only carries the deployment site's
    // tenant + rtId. Everything else (controller URI, broker host,
    // instancePrefix, …) is owned by the operator instance that services
    // this CR and read from OperatorOptions at startup. The user-facing
    // deployment site display name lives on the controller's RtDeploymentSite.Name attribute.
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("deploymentSiteRtId")]
    public string DeploymentSiteRtId { get; set; } = string.Empty;
}
