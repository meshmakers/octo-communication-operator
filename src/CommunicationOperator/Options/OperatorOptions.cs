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
    /// When true, the operator automatically creates DeploymentSite CRs
    /// when tenants are created (via PosCreateTenant distributed event).
    /// </summary>
    public bool AutoManageDeploymentSites { get; set; }

    /// <summary>
    /// Restricts the operator's CR watcher to a single Kubernetes namespace.
    /// When set, the operator only reconciles <c>DeploymentSite</c> resources
    /// in that namespace; CRs in other namespaces are ignored. When null or
    /// empty (the default), the operator watches all namespaces cluster-wide.
    /// Required when running multiple operator instances on the same cluster
    /// (e.g. one per target controller on an edge device) to prevent them
    /// from racing on the same CRs.
    /// </summary>
    public string? WatchNamespace { get; set; }

    /// <summary>
    /// Kubernetes namespace into which auto-created DeploymentSite CRs,
    /// per-tenant broker secrets, and adapter Deployments/Services are placed.
    /// All artefacts of a managed deployment site live in this namespace.
    /// </summary>
    public string DeploymentSiteNamespace { get; set; } = "octo";

    /// <summary>
    /// Kubernetes namespace that <b>adapter pool</b> workloads (AB#4924,
    /// <see cref="Meshmakers.Octo.Communication.Contracts.DataTransferObjects.WorkloadTypeDto.AdapterPool"/>)
    /// are deployed into. A pool member executes work on behalf of tenants that are not the
    /// tenant owning the deployment site, so it deliberately does not live where that tenant's own
    /// workloads live — consumption is attributed to the tenant whose work ran, not to the
    /// lender (concept §4b, Q1).
    ///
    /// 🔴 <b>When empty (the default) deployment site workloads land in <see cref="DeploymentSiteNamespace"/>.</b> That is
    /// not a fallback for lack of a better idea: Kubernetes forbids cross-namespace owner
    /// references, and a namespaced dependent whose owner lives in another namespace is treated
    /// as having a missing owner and is <i>deleted</i> by the garbage collector. The owner of a
    /// deployment site is the lending tenant's <c>DeploymentSite</c> CR, which lives in
    /// <see cref="DeploymentSiteNamespace"/> — so owner-reference garbage collection only exists while the
    /// two namespaces are the same one. <see cref="DeploymentSiteNamespace"/> is already a platform
    /// namespace rather than a tenant namespace, so the default satisfies both halves of Q1.
    ///
    /// Setting this to a different namespace is supported and moves the deployment site's Helm release
    /// there, but the operator then refuses to write the owner reference and says so once per
    /// deploy: an invalid cross-namespace reference would not merely fail to garbage-collect,
    /// it would have the deployment site deleted out from under a live tenant.
    /// </summary>
    public string? PlatformNamespace { get; set; }

    /// <summary>
    /// Cluster-internal URI of the communication controller service,
    /// used as the communicationControllerUri in auto-created DeploymentSite CRs.
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
    /// Interval in seconds between retry attempts for deployment site registrations
    /// that the controller rejected while the hub connection stayed alive
    /// (e.g. a transient CkCache error during a parallel service startup).
    /// Values &lt;= 0 disable the retry loop, leaving only the
    /// reconnect-driven re-registration. Fractional values are allowed
    /// (used by tests to drive the loop fast).
    /// </summary>
    public double DeploymentSiteRegistrationRetrySeconds { get; set; } = 30;

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
    /// Additional token issuer values workloads accept on top of
    /// <see cref="AuthUri"/> (split-horizon setups). When set, the operator
    /// injects the list into each workload's Helm values as
    /// <c>additionalValidIssuers</c>; the adapter chart renders them as
    /// <c>OCTO_ADAPTER__ADDITIONALVALIDISSUERS__&lt;n&gt;</c>, widening only the
    /// issuer STRING comparison (signing keys still come from
    /// <see cref="AuthUri"/>'s discovery document). Needed wherever callers
    /// obtain tokens under a different host name than the one the workloads
    /// reach the identity service by — e.g. the local kind dev cluster, where
    /// adapters use <c>https://host.docker.internal:5003</c> while tokens are
    /// minted via <c>https://localhost:5003/</c>. Env-bindable as
    /// <c>OPERATOR__ADDITIONALVALIDISSUERS__0</c> etc. Empty (default) projects
    /// nothing.
    /// </summary>
    public string[] AdditionalValidIssuers { get; set; } = [];

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
    /// Credentials the operator uses to obtain its own access token for the
    /// Communication Controller's <c>/operatorHub</c> (AB#5062). Optional: an
    /// operator with no client id connects exactly as it always has, without a
    /// token. See <see cref="OperatorAuthenticationOptions"/>.
    /// </summary>
    public OperatorAuthenticationOptions Authentication { get; set; } = new();

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

    /// <summary>
    /// How the operator invokes <c>helm</c> (AB#5325).
    /// </summary>
    public HelmOptions Helm { get; set; } = new();
}

/// <summary>
/// Helm invocation behaviour (AB#5325).
/// </summary>
public class HelmOptions
{
    /// <summary>
    /// When true, <c>helm upgrade</c> is given <c>--force-conflicts</c>, so a server-side apply takes
    /// ownership of fields another field manager holds instead of refusing. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>Off by default, and that is the decision, not an oversight.</b> The only way a
    ///         foreign manager appears on a workload this operator deploys is a person writing to the
    ///         object directly (<c>kubectl set image</c> / <c>set env</c> / <c>patch</c> all leave one
    ///         behind). Forcing past that overwrites what they set, silently, on every deploy from then
    ///         on — an operator may well want exactly that, but it has to be their call.
    ///     </para>
    ///     <para>
    ///         What it buys: without it such a workload is stuck. Server-side apply refuses, helm's own
    ///         rollback refuses for the same reason, and the release is left in <c>failed</c> state —
    ///         so a retry makes it worse. The alternative remedy needs no option: delete the Deployment
    ///         and let the chart own it again. Both are named in the error
    ///         (<see cref="Helm.HelmFieldOwnershipConflict" />).
    ///     </para>
    ///     <para>
    ///         Never applied to the pre-flight dry run: that asks "would this apply", and forcing its
    ///         way past a conflict would answer a question nobody asked.
    ///     </para>
    /// </remarks>
    public bool ForceConflicts { get; set; }
}

/// <summary>
/// Client-credentials configuration the operator authenticates its own
/// <c>/operatorHub</c> connection with (AB#5062).
/// </summary>
/// <remarks>
/// <para>
/// Bound from <c>Operator:Authentication</c>, i.e. <c>OPERATOR__AUTHENTICATION__ISSUERURI</c>,
/// <c>__CLIENTID</c>, <c>__CLIENTSECRET</c>, <c>__TENANTID</c>.
/// </para>
/// <para>
/// 🔴 <b>Every field is optional and the operator starts and connects without them.</b> This is a
/// deliberate compatibility guarantee, not an oversight: the operator is the control plane for
/// all workload management, and every installation in the estate — central and edge — currently
/// runs without any of these keys. A hard requirement here would take the whole fleet down on
/// upgrade, which is precisely the outage this work item exists to prevent. Unconfigured, the
/// operator connects anonymously exactly as before; the controller's <c>/operatorHub</c> gate
/// (AB#5059) must therefore stay in <c>LogOnly</c> until every operator has been given
/// credentials.
/// </para>
/// </remarks>
public class OperatorAuthenticationOptions
{
    /// <summary>
    /// Public issuer URI of the identity service (e.g. <c>https://connect.test-2.mm.cloud</c>).
    /// OIDC discovery runs against it, so it must be the address the identity service itself
    /// issues tokens under — not a cluster-internal service name whose discovery document would
    /// advertise a different issuer than the controller validates against.
    /// </summary>
    public string? IssuerUri { get; set; }

    /// <summary>
    /// Client id of the confidential OAuth client representing this operator. When empty, no
    /// token is acquired and the operator connects unauthenticated (see the remarks on
    /// <see cref="OperatorAuthenticationOptions"/>).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Client secret for <see cref="ClientId"/>. Supply it through a <c>secretKeyRef</c>-backed
    /// environment variable the same way <c>OPERATOR__BROKERPASSWORD</c> is supplied — never as a
    /// literal in a values file.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The tenant the operator authenticates <b>against</b>, sent as
    /// <c>acr_values=tenant:{TenantId}</c> on the token request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This is a deliberate identity decision, not an address.</b> The operator is
    /// tenant-crossing by construction: one process, one hub connection, every tenant's deployment sites and
    /// workloads. Accordingly <c>/operatorHub</c> is <b>not</b> tenant-scoped — the controller
    /// gates it with <c>SystemCommunicationApiPolicy</c>, which is a plain
    /// <c>scope=octo_api</c> requirement and never asks which tenant the caller belongs to. So the
    /// value here does not decide what the operator may do; it decides which tenant's
    /// <c>ClientStore</c> the identity service resolves <see cref="ClientId"/> in.
    /// </para>
    /// <para>
    /// Set it to the installation's <b>system tenant</b> (<c>OctoSystem</c> by default) and
    /// register the operator's client there. Two reasons, both structural: the operator's
    /// authority is system-level, and pinning it to one of the tenants it manages would let a
    /// tenant delete take the credential of the entire fleet with it — including the credential
    /// needed to tear that very tenant's deployment sites down.
    /// </para>
    /// <para>
    /// ⚠️ <b>Leaving it empty is only safe for a provably unmirrored client (AB#5058).</b> A
    /// <c>client_credentials</c> request without <c>acr_values</c> is refused outright with
    /// <c>invalid_request</c> as soon as the client id is ambiguous — flagged
    /// <c>AutoProvisionInChildTenants</c>, being a mirror itself, or having live
    /// <c>RtClientMirror</c> rows. Since a fleet-wide credential is exactly the kind of client
    /// somebody eventually flags for mirroring, always set this rather than relying on the
    /// unambiguous case holding forever. The operator logs a warning at startup when a client id
    /// is configured without one.
    /// </para>
    /// </remarks>
    public string? TenantId { get; set; }

    /// <summary>
    /// Whether enough is configured to attempt a token request at all. The secret is not part of
    /// the check — a public client has none, and a confidential client with a missing secret must
    /// fail loudly at the token endpoint rather than silently degrade to an anonymous connection
    /// that looks healthy until the gate is armed.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(IssuerUri) && !string.IsNullOrWhiteSpace(ClientId);
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
