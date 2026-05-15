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

    /// <summary>
    /// Cluster-internal URI of the reporting service. When set, the operator
    /// injects this into each workload's Helm values as
    /// <c>reportingServiceUri</c>. Leave empty in edge clusters that do not
    /// host a reporting service.
    /// </summary>
    public string? ReportingServiceUri { get; set; }

    /// <summary>
    /// Cluster-internal service endpoints (Mongo, RabbitMQ, CrateDB) that
    /// workloads need. Each field is optional: only those that are set are
    /// projected into the workload's Helm values at deploy time. Edge
    /// operators typically leave the cloud-side hosts empty and let the
    /// per-workload <c>ValuesYaml</c> supply local equivalents instead.
    /// </summary>
    public ClusterDependenciesOptions ClusterDependencies { get; set; } = new();

    /// <summary>
    /// Cluster-wide ingress defaults (class name, cert-manager issuer, TLS
    /// flag) that the operator injects into every workload chart that exposes
    /// an ingress. Per-workload <c>ValuesYaml</c> or structured overrides can
    /// still override individual annotations.
    /// </summary>
    public IngressDefaultsOptions Ingress { get; set; } = new();
}

/// <summary>
/// Cluster-internal service hostnames projected into workload Helm values.
/// </summary>
public class ClusterDependenciesOptions
{
    /// <summary>MongoDB connection string (comma-separated host:port list).</summary>
    public string? MongodbHost { get; set; }

    /// <summary>MongoDB replica set name. Optional.</summary>
    public string? MongodbReplicaSet { get; set; }

    /// <summary>RabbitMQ broker hostname (in-cluster service name).</summary>
    public string? RabbitMqHost { get; set; }

    /// <summary>RabbitMQ broker username.</summary>
    public string? RabbitMqUser { get; set; }

    /// <summary>CrateDB hostname (in-cluster service name).</summary>
    public string? StreamDataHost { get; set; }

    /// <summary>CrateDB username.</summary>
    public string? StreamDataUser { get; set; }
}

/// <summary>
/// Ingress defaults projected into workload Helm values.
/// </summary>
public class IngressDefaultsOptions
{
    /// <summary>Ingress class name (typically <c>nginx</c>).</summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// cert-manager ClusterIssuer name written into the
    /// <c>cert-manager.io/cluster-issuer</c> ingress annotation.
    /// </summary>
    public string? ClusterIssuer { get; set; }

    /// <summary>
    /// When set, projects <c>ingress.tls</c> on the workload values.
    /// Null leaves the chart default in place.
    /// </summary>
    public bool? Tls { get; set; }
}
