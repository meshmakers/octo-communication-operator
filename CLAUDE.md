# CLAUDE.md

Instructions for coding agents in this repository. Design rationale lives in `docs/`; read the
matching file before changing the code it covers.

## What this repo is

A Kubernetes operator (KubeOps) that reconciles `CommunicationPool` CRs into Helm releases for mesh
Adapters and Applications, and talks to the Communication Controller over the SignalR `/operatorHub`.

- The CRD ships from `octo-helm-core` (`octo-mesh-crds` chart). Do not add or edit CRD YAML in this
  repo; regenerate with the command in `README.md` -> "Generate CRD and deployment files".
- `V1CommunicationPoolEntity`, group `octo-mesh.meshmakers.io`, version `v1alpha1`.
- There is no raw-K8s `AdapterReconciler`. Every Adapter and Application is deployed via Helm.
- Central mode (`OPERATOR__AUTOMANAGEPOOLS=true`) auto-creates and deletes CRs; edge mode (`false`)
  does not. `OPERATOR__COMMUNICATIONCONTROLLERURI` is required in **both** modes - the SignalR
  connection is not gated on `AutoManagePools`.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `tests/CommunicationOperator.Tests/E2E/**`, `start-operator.ps1` | `docs/E2E-SMOKE-TEST.md` |
| `src/CommunicationOperator/Controller/**`, `src/CommunicationOperator/Program.cs` | `docs/http-surface.md` |
| `src/CommunicationOperator/Services/**` | `docs/operator-hub.md` |
| `src/CommunicationOperator/Reconcilers/**`, `src/CommunicationOperator/Helm/**` | `docs/reconcilers.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md` (CK model and deploy
contract). The `/operatorHub` contract itself lives in
`octo-communication-controller-services/CLAUDE.md`.

## Build & test

```bash
# DebugL = local development against the monorepo NuGet cache at ../nuget/
dotnet build Octo.CommunicationOperator.sln -c DebugL

# Canonical: same form the Azure Pipeline runs.
# The `--` separates SDK args from Microsoft.Testing.Platform args.
dotnet test --solution Octo.CommunicationOperator.sln -c DebugL -- --report-trx --report-trx-filename test-results.trx

# Quick form during development (no TRX, no build):
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build

# Single test class
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build -- \
    --treenode-filter "/*/*/CommunicationPoolValidatorTests/*"
```

`dotnet test` runs through Microsoft.Testing.Platform (`global.json` sets
`"test": { "runner": "Microsoft.Testing.Platform" }`; the VSTest path is rejected on the .NET 10 SDK).
Two consequences when you edit a command:

- Pass the project or solution as a flag (`--project` / `--solution`). A positional argument is
  rejected.
- Put reporter and filter arguments after `--`.

## Before you commit

1. `dotnet build Octo.CommunicationOperator.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true`).
2. `dotnet test --solution Octo.CommunicationOperator.sln -c DebugL -- --report-trx --report-trx-filename test-results.trx`
   reports 0 failed.
3. Update the docs the change touches:
   - changed a config key -> the table in `README.md`
   - changed reconciler or Helm behaviour -> `docs/reconcilers.md`
   - changed hub, registration or token behaviour -> `docs/operator-hub.md`
   - changed `OperatorHubService` or `CommunicationPoolManager` -> run `docs/E2E-SMOKE-TEST.md`

## Rules

- **Do not add `[Authorize]` to a controller in this repo.** `Program.cs` registers no
  authentication scheme, so the attribute makes every request to that endpoint fail with
  `InvalidOperationException: No authenticationScheme was specified`. Gating an endpoint here means
  bringing an authority, an OIDC client and a bearer scheme with it first.
- **Do not add `[ApiVersion]`.** Routes are literal `system/v1/[controller]`; `Asp.Versioning` is not
  wired into this host and the `v1` in the template is the version pin.
- **New HTTP endpoints are gated by remote address, not by attributes.** Copy the loopback check in
  `Controller/DiagnosticsController`: unmap IPv4-mapped IPv6 (`::ffff:127.0.0.1`) before testing, and
  treat a missing remote address as not loopback.
- **Add new Kubernetes calls to `ICommunicationPoolKubernetesGateway`.** Do not call `IKubernetes` or
  its extension methods from anywhere else.
- **Never rethrow out of a SignalR hub callback or a reconciler.** Failures are logged and reported in
  the ack; one bad event must not drop the hub connection.
- **Operator authentication stays optional.** With no `OPERATOR__AUTHENTICATION__CLIENTID` the
  operator connects anonymously, which is what every installation in the estate does today. Do not
  make any of the `Authentication` keys required.
- **Redeploy must not resurrect a hibernated workload** - see `docs/reconcilers.md`, scale verb.
- **`OctoNugetPrivateServer` must reach every MSBuild invocation in `azure-pipelines.yml`** (explicit
  `Restore` -> `Build` -> `Test` steps). `Directory.Build.props` reads it to pick `OctoVersion` and
  `RestoreSources`; a step that misses it falls back to nuget.org and drags in stale transitive
  packages (`NU1902` under `TreatWarningsAsErrors`).
- **Use `command: 'custom'` + `--solution` in the pipeline**, not `command: 'test'` + `projects:` -
  the latter passes a positional glob, which MTP rejects. `--solution` picks up every test project in
  the `.sln`.

## Webhooks

- `CommunicationPoolValidator`: `Spec.PoolRtId` must be a 24-character lowercase hex MongoDB
  ObjectId. `Spec.PoolName` is optional - the rtId is the canonical identity and every derived k8s
  name is built from it via `K8sNaming.DnsName`.
- `CommunicationPoolMutator` is a no-op (`NoChanges()`).

## Testing conventions

- **Framework**: TUnit (`[Test]`, `Assert.That(...).IsXxx(...)`), NSubstitute for mocking.
- **Layout**: tests mirror the source folders (`Webhooks/`, `Services/`, `Common/`, `Finalizer/`).
- **Namespaces**: `Meshmakers.Octo.Communication.Operator.Tests.<Area>`.
- **Async throws on substitutes**: use `ThrowsAsync(...)`, not `Throws(...)`, for `Task`-returning
  members - `NS5003` is an error.
- **Disposable SUTs**: implement `IDisposable` on the test class and dispose the field, or TUnit
  raises `TUnit0023`.
- **`OperatorOptions` in tests**: `Microsoft.Extensions.Options.Options.Create(new OperatorOptions { ... })`,
  fully qualified - `using ...Operator.Options;` shadows `Options.Create`.
- **New test projects** must set `<EnablePreviewFeatures>true</EnablePreviewFeatures>` (KubeOps APIs
  are `[RequiresPreviewFeatures]`, CA2252).

## Configuration

`OperatorOptions` binds the `Operator` section; every key is also an environment variable prefixed
`OPERATOR__`. **The full table lives in `README.md` -> Configuration - edit it there, not here.**

Four that are easy to get wrong:

- `WatchNamespace` - empty watches all namespaces. Required when several operators share a cluster,
  or they race on each other's CRs.
- `WorkloadCommunicationControllerUri` - empty projects `CommunicationControllerUri` into workloads.
  Set it only when workloads cannot use the operator's own address: on local kind the operator
  reaches a host-run controller through a pod hostAlias that the adapters do not have, and they
  sit at `Unregistered` while the operator looks healthy.
- `PlatformNamespace` - empty resolves to `PoolNamespace`, which is what owner-reference garbage
  collection requires. A distinct namespace disables the owner reference.
- `ClusterDependencies.SystemDatabaseName` / `StreamDataSchemaInstancePrefix` - must mirror the core
  services of the same instance, or workloads fail every CK-model load and read the wrong CrateDB
  schemas. Both empty on a single-instance cluster.
