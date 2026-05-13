# OctoMesh Communication Operator

The Communication Operator is a Kubernetes operator that manages Helm-based mesh workload deployments. It watches `CommunicationPool` custom resources and runs `helm upgrade --install` / `helm uninstall` for each Adapter and Application managed by the pool, driven by events the Communication Controller fires on the `/operatorHub` SignalR channel.

The operator supports both **edge deployment** (running on remote edge clusters connecting to a central controller) and **central deployment** (running alongside the Communication Controller in the same cluster, where CRs are auto-created on tenant creation).

## Configuration

The operator can be configured via environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `OPERATOR__AUTOMANAGEPOOLS` | Auto-create CommunicationPool CRs on tenant creation | `false` |
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

For central deployment, the operator also requires RabbitMQ connectivity for receiving tenant lifecycle events via the DistributedEventHub (configured via `Meshmakers.Octo.Services.Infrastructure`).

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

