namespace Meshmakers.Octo.Communication.Operator.Options;

/// <summary>
/// Configuration options for the Communication Operator.
/// </summary>
public class OperatorOptions
{
    /// <summary>
    /// Name of the Kubernetes image pull secret to use for adapter pod deployments.
    /// When set, this secret is added to the pod spec of created adapter deployments.
    /// </summary>
    public string? ImagePullSecretName { get; set; }

    /// <summary>
    /// When true, the operator automatically creates CommunicationPool CRs
    /// when tenants are created (via PosCreateTenant distributed event).
    /// </summary>
    public bool AutoManagePools { get; set; }

    /// <summary>
    /// Kubernetes namespace into which auto-created CommunicationPool CRs,
    /// per-tenant broker secrets, and adapter Deployments/Services are placed.
    /// All artefacts of a managed pool live in this namespace.
    /// </summary>
    public string PoolNamespace { get; set; } = "octo";

    /// <summary>
    /// Cluster-internal URI of the communication controller service,
    /// used as the communicationControllerUri in auto-created CommunicationPool CRs.
    /// </summary>
    public string CommunicationControllerUri { get; set; } = string.Empty;

    /// <summary>
    /// Whether adapter pods should ignore certificate validation.
    /// </summary>
    public bool AdapterIgnoreCertificateValidation { get; set; }

    /// <summary>
    /// RabbitMQ broker host for adapter pods.
    /// </summary>
    public string BrokerHost { get; set; } = string.Empty;

    /// <summary>
    /// RabbitMQ broker virtual host.
    /// </summary>
    public string BrokerVirtualHost { get; set; } = "/";

    /// <summary>
    /// RabbitMQ broker port.
    /// </summary>
    public int BrokerPort { get; set; } = 5672;

    /// <summary>
    /// RabbitMQ broker username for adapter pods.
    /// </summary>
    public string? BrokerUser { get; set; }

    /// <summary>
    /// RabbitMQ broker password for adapter pods.
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    /// Instance prefix for the OctoMesh installation.
    /// </summary>
    public string? InstancePrefix { get; set; }

    /// <summary>
    /// Hostname or IP address where the operator's webhook endpoints are
    /// reachable from the Kubernetes API server. Used only in DEBUG/DEBUGL
    /// builds when KubeOps auto-registers webhook configurations against
    /// this address. When null or empty, the operator picks the first
    /// non-loopback IPv4 address of the host at startup.
    /// </summary>
    public string? DevWebhookHost { get; set; }

    /// <summary>
    /// Port the dev webhook endpoint binds to (HTTPS). Must match the port
    /// the registered <c>MutatingWebhookConfiguration</c> /
    /// <c>ValidatingWebhookConfiguration</c> use. Defaults to 6001.
    /// </summary>
    public ushort DevWebhookPort { get; set; } = 6001;
}
