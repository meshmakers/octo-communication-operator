# OctoMesh Communication Operator

The Communication Operator is a Kubernetes operator that manages Helm-based mesh workload deployments. It watches `CommunicationPool` custom resources and runs `helm upgrade --install` / `helm uninstall` for each Adapter and Application managed by the pool, driven by events the Communication Controller fires on the `/operatorHub` SignalR channel.

The operator supports both **edge deployment** (running on remote edge clusters connecting to a central controller) and **central deployment** (running alongside the Communication Controller in the same cluster, where CRs are auto-created on tenant creation).

## Configuration

The operator can be configured via environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `OPERATOR__AUTOMANAGEPOOLS` | Auto-create CommunicationPool CRs on tenant creation | `false` |
| `OPERATOR__WATCHNAMESPACE` | Restricts the CR watcher to a single namespace. Required when multiple operator instances share one cluster (e.g. edge devices running one operator per target controller) so they don't race on each other's CRs. Leave empty to watch all namespaces. | _(empty — watch all)_ |
| `OPERATOR__POOLNAMESPACE` | Namespace where auto-created CRs and per-tenant broker secrets live. Helm releases default to the same namespace unless the chart overrides it. | `octo` |
| `OPERATOR__COMMUNICATIONCONTROLLERURI` | Controller URI for auto-created CRs | _(required when AutoManagePools=true)_ |
| `OPERATOR__DEFAULTPOOLNAME` | Pool name for auto-created CRs | `default` |
| `OPERATOR__INSTANCEPREFIX` | Instance prefix forwarded to workload pods via the Helm chart values | _(none)_ |
| `OPERATOR__ADAPTERIGNORECERTIFICATEVALIDATION` | Forwarded to workload pods via the Helm chart values (dev only) | `false` |
| `OPERATOR__BROKERHOST` | RabbitMQ host for workload pods | _(required when AutoManagePools=true)_ |
| `OPERATOR__BROKERVIRTUALHOST` | RabbitMQ virtual host | `/` |
| `OPERATOR__BROKERPORT` | RabbitMQ port | `5672` |
| `OPERATOR__BROKERUSER` | RabbitMQ username for broker secrets | _(required when AutoManagePools=true)_ |
| `OPERATOR__BROKERPASSWORD` | RabbitMQ password for broker secrets | _(required when AutoManagePools=true)_ |
| `OPERATOR__ROOTCACERTIFICATE` | PEM-encoded root CA certificate (chain) the operator's own pod trusts (chart value `secrets.rootCa`, forwarded here from the chart's `{fullname}-ca` Secret). When set, injected as a plain-string `secrets.rootCa` value into every deployed workload, unconditionally — see AB#4417 below. | _(none)_ |
| `OPERATOR__REPORTINGSERVICEURI` | Cluster-internal URI of the reporting service. Projected into each workload's Helm values as `reportingServiceUri`. | _(none)_ |
| `OPERATOR__AUTHURI` | Public URI of the identity service issuing the access tokens secured trigger nodes accept. Projected into each workload's Helm values as `authUri`. Must be the public issuer address, not a cluster-internal service name — the adapter compares it against the token's `iss` claim. | _(none)_ |
| `OPERATOR__AUTHENTICATION__ISSUERURI` | Public issuer URI of the identity service the operator obtains its **own** access token from, for the `/operatorHub` connection (AB#5062). Optional — see [Operator hub authentication](#operator-hub-authentication-ab5062). | _(none)_ |
| `OPERATOR__AUTHENTICATION__CLIENTID` | Client id of the confidential OAuth client representing this operator. Empty ⇒ the operator connects without a token, exactly as before. | _(none)_ |
| `OPERATOR__AUTHENTICATION__CLIENTSECRET` | Client secret for that client. Supply via `secretKeyRef`, like `OPERATOR__BROKERPASSWORD`. | _(none)_ |
| `OPERATOR__AUTHENTICATION__TENANTID` | Tenant the operator authenticates **against** (`acr_values=tenant:…`). Set it to the installation's **system tenant** (`OctoSystem` by default). | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__MONGODBHOST` | MongoDB connection string projected into workload `clusterDependencies.mongodbHost`. | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__MONGODBREPLICASET` | MongoDB replica-set name projected into workload `clusterDependencies.mongodbReplicaSet`. | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__RABBITMQHOST` | RabbitMQ host projected into workload `clusterDependencies.rabbitMqHost`. | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__RABBITMQUSER` | RabbitMQ user projected into workload `clusterDependencies.rabbitMqUser`. | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__STREAMDATAHOST` | CrateDB host projected into workload `clusterDependencies.streamDataHost`. | _(none)_ |
| `OPERATOR__CLUSTERDEPENDENCIES__STREAMDATAUSER` | CrateDB user projected into workload `clusterDependencies.streamDataUser`. | _(none)_ |
| `OPERATOR__INGRESS__CLASSNAME` | Ingress class projected into workload `ingress.className`. | _(none)_ |
| `OPERATOR__INGRESS__CLUSTERISSUER` | cert-manager ClusterIssuer projected into workload `ingress.annotations["cert-manager.io/cluster-issuer"]`. | _(none)_ |
| `OPERATOR__INGRESS__TLS` | TLS flag projected into workload `ingress.tls`. Leave unset to keep the chart default. | _(unset)_ |
| `OPERATOR__INGRESS__ANNOTATIONS__<n>__NAME` / `__VALUE` | Additional ingress annotations projected into workload `ingress.annotations` (indexed name/value pairs because annotation keys contain dots/slashes, e.g. `nginx.ingress.kubernetes.io/proxy-body-size`). An entry named like the cluster-issuer annotation overrides `OPERATOR__INGRESS__CLUSTERISSUER`. | _(none)_ |
| `OPERATOR__CLUSTERSECRETS__MONGODBUSERPASSWORD` | MongoDB user password injected as secret-flagged override `secrets.databaseUser` when the workload's `ReceivesClusterSecrets` flag is true. | _(none)_ |
| `OPERATOR__CLUSTERSECRETS__MONGODBADMINPASSWORD` | MongoDB admin password injected as `secrets.databaseAdmin` when the flag is true. | _(none)_ |
| `OPERATOR__CLUSTERSECRETS__STREAMDATAPASSWORD` | CrateDB password injected as `secrets.streamDataPassword` when the flag is true. | _(none)_ |

The cluster-dependency, reporting-URI and ingress fields are all optional. Each value that is set is injected into every deployed workload's Helm values as the **lowest** precedence layer — the workload's own `ValuesYaml` and structured overrides win over it. Edge operators typically leave the cloud-side dependency hosts empty so per-workload values supply edge-local equivalents.

The `ClusterSecrets.*` settings are different: they are injected only when the workload itself opts in via the `ReceivesClusterSecrets` flag on its CK Adapter entity. They appear in the rendered manifest as `valueFrom.secretKeyRef` envelopes pointing at the per-release operator-managed Secret (`{release}-octo-secrets`), not as plain values. The adapter chart's `secrets.*` paths must accept both plaintext strings (legacy) and `valueFrom` maps — see the `octo-mesh.secretEnv` helper in `octo-mesh-adapter` / `octo-eda-adapter` chart templates.

`BrokerPassword` and `RootCaCertificate` are injected unconditionally instead — independent of `ReceivesClusterSecrets` — because every workload needs the controller command bus and, on private-CA clusters, the same TLS trust anchor the operator itself was given (AB#4417). `BrokerPassword` still renders as a `valueFrom.secretKeyRef` envelope like the gated secrets above; `RootCaCertificate` is the one exception that renders as a plain string at `secrets.rootCa` — the workload chart's own root-CA handling `b64enc`s the value directly and requires a literal, not a `valueFrom` map.

For central deployment, the operator also requires RabbitMQ connectivity for receiving tenant lifecycle events via the DistributedEventHub (configured via `Meshmakers.Octo.Services.Infrastructure`).

### Operator hub authentication (AB#5062)

The operator can present a client-credentials access token on its `/operatorHub` connection to the
Communication Controller. It is acquired at startup and refreshed for the lifetime of the process,
and the SDK reads it on every (re)connect.

**Configuring it is optional and the default is the previous behaviour.** With no
`OPERATOR__AUTHENTICATION__CLIENTID` the operator starts and connects anonymously, precisely as
every installation does today. This is deliberate: the operator is the control plane for all
workload management, so a hard requirement would take the whole fleet down on upgrade.

```yaml
Operator:
  Authentication:
    IssuerUri: https://connect.test-2.mm.cloud   # public issuer, not a cluster-internal name
    ClientId: octo-communication-operator
    ClientSecret: <from a secretKeyRef>
    TenantId: OctoSystem                          # the system tenant — see below
```

**Which tenant, and why it matters.** The operator is tenant-crossing: one process, one connection,
every tenant's pools. `/operatorHub` is correspondingly *not* tenant-scoped — the controller gates it
with `SystemCommunicationApiPolicy`, a plain `scope=octo_api` requirement that never asks which
tenant the caller belongs to. `TenantId` therefore does not decide what the operator may do; it
decides which tenant's `ClientStore` the identity service resolves `ClientId` in. Register the
operator's client in the **system tenant** and name it here. Pinning the credential to one of the
managed tenants would let a tenant delete take the whole fleet's credential with it — including the
credential needed to tear that tenant's own pools down.

Since **AB#5058**, omitting `TenantId` is only safe for a client that is provably unmirrored: a
`client_credentials` request without `acr_values` is refused with `invalid_request` as soon as the
client id is ambiguous (flagged `AutoProvisionInChildTenants`, a mirror itself, or with live mirror
rows). Always set it. The operator logs a warning at startup when a client id is configured without
one.

**Token renewal.** An established SignalR connection is authorized once, at connect time, and the
controller does not set `CloseOnAuthenticationExpiration` — so an expiring token does not drop a live
connection. The exposure is the *re*connect, which happens routinely (controller rollout, node drain,
network blip, SDK watchdog). `OperatorAccessTokenService` therefore replaces the token five minutes
before its own expiry; a failed acquisition retries every 30 seconds and keeps the previous token in
the meantime.

**Rollout order.** The controller's `/operatorHub` gate (AB#5059) ships in `LogOnly`. Roll operators
with credentials out **first**, read the controller's `LogOnly` warnings until no operator connection
is listed as failing the policy any more, and only then set
`OCTO_OPERATORHUBAUTHORIZATION__MODE=Enforce` on the controller. Arming it earlier disconnects every
operator that has not been given a credential yet — central and edge alike — leaving all pools
unregistered and no workload deploys.

## Getting started as developer

Install kind (see for full documentation [here](https://kind.sigs.k8s.io/docs/user/quick-start/))

On macOS via Homebrew:
```bash
brew install kind
```

On Windows via Chocolatey (https://chocolatey.org/packages/kind)
```pwsh
choco install kind
```

On Windows via Winget (https://github.com/microsoft/winget-pkgs/tree/master/manifests/k/Kubernetes/kind)
```pwsh
winget install Kubernetes.kind
```

Create a cluster:
```bash
./src/scripts/Create-KindTestCluster.ps1
```

Connect to the cluster:
```bash
kubectl cluster-info --context kind-kind
```

Install the CRDs (located in the `octo-helm` repository):
```bash
cd ~/meshmakers/octo-helm/src 
helm install --namespace octo-operator-system --create-namespace octo-mesh-crds ./octo-mesh-crds/
```

No source edits are needed for the dev webhook endpoint. In DEBUG/DEBUGL
builds the operator picks the first non-loopback IPv4 address of the host
at startup and generates a self-signed TLS cert + KubeOps webhook
configuration against it. The chosen address is logged on the first line:

```
INFO Dev webhook endpoint: https://192.168.x.y:6001
```

To override (multi-NIC hosts, VPN-only setups), set either of:

- `Operator:DevWebhookHost` and `Operator:DevWebhookPort` in
  `src/CommunicationOperator/appsettings.Development.json`
- `OCTO_OPERATOR__DEVWEBHOOKHOST` and `OCTO_OPERATOR__DEVWEBHOOKPORT`
  environment variables

Run the operator in debug mode

Apply secret and the pool to create a first communication:
```bash
kubectl create ns pool1
kubectl -n pool1 apply -f ./src/scripts/test-cluster-secret-local.yaml
kubectl -n pool1 apply -f ./src/scripts/test-cluster-pool-local.yaml
```


## During development


# Generate CRD and deployment files
```bash
dotnet kubeops g op meshmakers-octo-communication-operator ./CommunicationOperator.csproj --out config --clear-out
```

## Tests

Unit tests live in `tests/CommunicationOperator.Tests/` and use **TUnit** with **NSubstitute** for mocking, matching the convention of the sibling `octo-communication-controller-services` repository.

Build the solution and run the test suite:

```bash
# Canonical (same as Azure Pipeline):
dotnet test --solution Octo.CommunicationOperator.sln -c DebugL -- --report-trx --report-trx-filename test-results.trx

# Quick form during development:
dotnet build Octo.CommunicationOperator.sln -c DebugL
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build
```

The test runner is opted into Microsoft.Testing.Platform via `global.json` at the repo root. See `CLAUDE.md` for details about the .NET 10 / MTP arguments.

