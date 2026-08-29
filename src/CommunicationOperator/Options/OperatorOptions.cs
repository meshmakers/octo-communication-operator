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
    /// Restricts the operator's CR watcher to a single Kubernetes namespace.
    /// When set, the operator only reconciles <c>CommunicationPool</c> resources
    /// in that namespace; CRs in other namespaces are ignored. When null or
    /// empty (the default), the operator watches all namespaces cluster-wide.
    /// Required when running multiple operator instances on the same cluster
    /// (e.g. one per target controller on an edge device) to prevent them
    /// from racing on the same CRs.
    /// </summary>
    public string? WatchNamespace { get; set; }

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
    /// Controller URI projected into the Helm values of every deployed workload. When empty
    /// (the default), <see cref="CommunicationControllerUri"/> is projected — one address serves
    /// both the operator's own hub connection and the workloads, which is correct wherever both
    /// resolve the same name the same way. They stop doing that as soon as the operator needs an
    /// address the workloads cannot use: observed on a local kind cluster, where the operator
    /// reached a host-run controller through a name only it could be given, and every adapter
    /// silently sat at Unregistered while the operator looked healthy (AB#4967). This option
    /// splits the two consumers without changing any existing installation.
    /// </summary>
    public string WorkloadCommunicationControllerUri { get; set; } = string.Empty;

    /// <summary>
    /// Interval in seconds between retry attempts for pool registrations
    /// that the controller rejected while the hub connection stayed alive
    /// (e.g. a transient CkCache error during a parallel service startup).
    /// Values &lt;= 0 disable the retry loop, leaving only the
    /// reconnect-driven re-registration. Fractional values are allowed
    /// (used by tests to drive the loop fast).
    /// </summary>
    public double PoolRegistrationRetrySeconds { get; set; } = 30;

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
    /// PEM-encoded root CA certificate (chain) the operator's own pod was
    /// given via the chart's <c>secrets.rootCa</c> value (see the
    /// operator chart's <c>{fullname}-ca</c> Secret, forwarded here as an
    /// environment variable backed by a <c>secretKeyRef</c>). Set only on
    /// clusters whose ingress/controller endpoint uses a private CA (e.g.
    /// the local kind getting-started quickstart). When set, the operator
    /// injects this same PEM as <c>secrets.rootCa</c> into every deployed
    /// workload's Helm values — unconditionally, like
    /// <see cref="BrokerPassword"/> — because a workload that talks TLS to
    /// the Communication Controller needs the same trust anchor the
    /// operator itself was given, regardless of whether it also opts into
    /// <c>ReceivesClusterSecrets</c>. Unlike <see cref="BrokerPassword"/>,
    /// the value is injected as a plain string, not a secret-flagged
    /// override: the workload chart's own <c>secrets.rootCa</c> handling
    /// (its <c>{fullname}-ca</c> Secret template) <c>b64enc</c>s the value
    /// directly and requires a literal string, not a
    /// <c>valueFrom.secretKeyRef</c> map.
    /// </summary>
    public string? RootCaCertificate { get; set; }

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
    /// Public URI of the identity service issuing the access tokens that
    /// secured trigger nodes accept. When set, the operator injects this into
    /// each workload's Helm values as <c>authUri</c>. This must be the public
    /// issuer address rather than a cluster-internal service name: the adapter
    /// uses it as the expected issuer of the token, not merely as an address to
    /// fetch signing keys from. Leave empty in clusters whose workloads expose
    /// no secured routes.
    /// </summary>
    public string? AuthUri { get; set; }

    /// <summary>
    /// Private container registry the cluster uses to pull workload images
    /// (e.g. <c>docker.mm.cloud</c>). When set, the operator projects this
    /// into each workload's Helm values as <c>image.privateRegistry</c>, so
    /// the chart renders the image reference as
    /// <c>{registry}/{repository}:{tag}</c> instead of pulling from
    /// docker.io. Leave empty when the cluster pulls from a public
    /// registry.
    /// </summary>
    public string? ImageRegistry { get; set; }

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

    /// <summary>
    /// Cluster-internal data-store credentials (Mongo + CrateDB) the operator
    /// injects into a workload's Helm values as secret-flagged value
    /// overrides when the workload's Adapter entity opts in via
    /// <c>ReceivesClusterSecrets</c>. The RabbitMQ broker password is NOT in
    /// here — it lives on <see cref="BrokerPassword"/> and is always injected
    /// because every adapter needs the controller command bus regardless of
    /// whether it also touches data stores. Pair this with secrets in the
    /// operator's Kubernetes namespace (typically populated by the deployment
    /// pipeline from Vault).
    /// </summary>
    public ClusterSecretsOptions ClusterSecrets { get; set; } = new();
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

    /// <summary>
    /// MongoDB system database holding the tenant registry (Epic AB#4944 instance isolation).
    /// Must match the core services' <c>serviceDefaults.systemDatabaseName</c>: a workload
    /// resolves its tenant through this database, so an instance running on a non-default
    /// system database gets "Tenant '…' does not exist" on every CK-model load without it.
    /// Empty keeps the workload's compiled-in default (<c>OctoSystem</c>).
    /// </summary>
    public string? SystemDatabaseName { get; set; }

    /// <summary>
    /// CrateDB schema instance prefix (AB#4946). Must match the core services'
    /// <c>clusterDependencies.streamDataSchemaInstancePrefix</c> — without it a second
    /// instance's workloads read and write the unprefixed schemas of the first one.
    /// Empty keeps the legacy, unprefixed schema names.
    /// </summary>
    public string? StreamDataSchemaInstancePrefix { get; set; }
}

/// <summary>
/// Cluster-side credentials the operator can inject into opted-in workloads.
/// </summary>
public class ClusterSecretsOptions
{
    /// <summary>MongoDB user password (the non-admin runtime user).</summary>
    public string? MongodbUserPassword { get; set; }

    /// <summary>MongoDB admin password.</summary>
    public string? MongodbAdminPassword { get; set; }

    /// <summary>CrateDB password for the stream-data user.</summary>
    public string? StreamDataPassword { get; set; }
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

    /// <summary>
    /// Additional ingress annotations projected into every workload's
    /// <c>ingress.annotations</c> (e.g. nginx proxy-body-size / timeouts).
    /// Modeled as a list of name/value pairs because annotation keys contain
    /// dots and slashes, which cannot appear in environment-variable names —
    /// the chart binds entries as <c>OPERATOR__INGRESS__ANNOTATIONS__n__NAME</c> /
    /// <c>__VALUE</c>. An entry named like the cluster-issuer annotation wins
    /// over <see cref="ClusterIssuer"/>.
    /// </summary>
    public List<IngressAnnotationOption> Annotations { get; set; } = [];
}

/// <summary>
/// A single ingress annotation name/value pair (see
/// <see cref="IngressDefaultsOptions.Annotations"/>).
/// </summary>
public class IngressAnnotationOption
{
    /// <summary>Annotation key, e.g. <c>nginx.ingress.kubernetes.io/proxy-body-size</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Annotation value, e.g. <c>25m</c>.</summary>
    public string? Value { get; set; }
}
