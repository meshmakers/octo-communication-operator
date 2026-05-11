# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

The **Octo Communication Operator** is a Kubernetes operator that manages mesh adapter deployments via the [KubeOps](https://github.com/buehler/dotnet-operator-sdk) framework. It watches `CommunicationPool` custom resources and, optionally, connects to the Communication Controller via SignalR to receive tenant lifecycle events and auto-create/auto-delete `CommunicationPool` CRs.

It supports two deployment modes:

- **Edge deployment**: the operator runs on a remote edge cluster. `CommunicationPool` CRs are managed manually (or by an external system); the operator only reconciles existing CRs into adapter deployments and services.
- **Central deployment**: the operator runs alongside the Communication Controller in the same cluster. With `OPERATOR__AUTOMANAGEPOOLS=true` it connects to the Controller's `/operatorHub` SignalR hub and creates/deletes `CommunicationPool` CRs and broker secrets in response to `TenantCreated` / `TenantDeleted` events.

## Solution Layout

```
Octo.CommunicationOperator.sln
├── src/CommunicationOperator/                Operator host (ASP.NET Core, Microsoft.NET.Sdk.Web)
│   ├── Common/        DictionaryExtensions, OperatorLog (LoggerMessage source-gen)
│   ├── Controller/    HTTP controllers (CommunicationPool, Diagnostics)
│   ├── Entities/      V1CommunicationPoolEntity (CRD-mapped)
│   ├── Finalizer/     CommunicationPoolFinalizer
│   ├── Models/        Pool, K8Pool, PoolDescriptor (DTO-side models)
│   ├── Options/       OperatorOptions (configuration binding)
│   ├── Reconcilers/   AdapterReconciler (creates Deployments + Services per adapter)
│   ├── Services/      CommunicationPoolManager, OperatorHubService, PoolService, DiagnosticsService
│   ├── Webhooks/      CommunicationPoolValidator, CommunicationPoolMutator (admission webhooks)
│   └── scripts/       kind cluster bootstrap scripts
└── tests/CommunicationOperator.Tests/        Unit tests (TUnit + NSubstitute)
```

The CRD is shipped from the `octo-helm-core` repository (`octo-mesh-crds` chart), not from this repo. Generation of CRD YAMLs is documented in `README.md` ("Generate CRD and deployment files").

## Architecture Concepts

### Custom Resource: `CommunicationPool`

The operator's primary resource is `V1CommunicationPoolEntity` (group `octo-mesh.meshmakers.io`, version `v1alpha1`). The spec carries the tenant identity, controller endpoint, and broker connection parameters that adapter pods need.

### Reconciliation Flow

1. A `CommunicationPool` CR is created (manually, or via `OperatorHubService` when `AutoManagePools=true`).
2. The operator's pool service registers the pool with the Communication Controller via the `PoolHub` SignalR client.
3. The Controller pushes the list of adapters to deploy into the pool.
4. `AdapterReconciler` creates/updates Kubernetes `Deployment` + `Service` objects for each adapter, reading the broker credentials from the `<tenantId>-<poolName>-octo-mesh-connection` `Secret`.
5. On pool deletion, all adapter deployments and services labelled with the pool are removed.

`PoolService.UnRegisterPoolAsync` (called from `CommunicationPoolController.DeletedAsync`) treats any `HubException` from the controller-side `UnregisterPoolOperatorAsync` call as a **soft failure** and only logs it. Reason: the CR is already gone when `DeletedAsync` fires, and during the tenant-delete cascade the tenant itself no longer exists at the controller — so the unregister roundtrip will respond with `TenantException`. Re-throwing would put the entity back in the KubeOps retry queue forever. The local connection is still stopped and the pool removed from `_pools` regardless.

### Helm Workload Reconciliation (Phase 3)

Workloads (Adapters + Applications) deployed to a Cloud pool are driven by
the `WorkloadReconciler` over the `helm` CLI, fully decoupled from the old
raw-K8s `AdapterReconciler`.

**Layers:**
- `Helm/IHelmProcessInvoker` + `HelmProcessInvoker` — low-level
  `System.Diagnostics.Process` wrapper around the `helm` binary on PATH.
  Captures stdout / stderr, masks `--username` / `--password` values from
  the debug log line.
- `Helm/IHelmRunner` + `HelmRunner` — high-level operations:
  `EnsureRepoAsync` (idempotent `helm repo add --force-update` + `helm repo update`),
  `UpgradeInstallAsync` (with `-f`, `--set`, `--atomic`, `--create-namespace`),
  `UninstallAsync` (uses `--ignore-not-found`). Non-zero exit codes become
  `HelmException` with full stderr.
- `Reconcilers/WorkloadOverrideYamlBuilder` — turns the structured
  `ValueOverride[]` from the controller into a `values-overrides.yaml`
  file. Secret-flagged entries become a `valueFrom: secretKeyRef`
  envelope pointing at the operator-owned `{release}-octo-secrets`
  Kubernetes Secret. Non-secret entries become literal values. Nested
  dotted paths (e.g. `oauth.clientSecret`) become nested maps.
- `Reconcilers/WorkloadReconciler` — the orchestrator:
  1. `ReconcileSecretAsync` materializes / refreshes / removes the
     operator-owned K8s Secret for the workload's secret values.
  2. `EnsureRepoAsync` registers the chart repository (alias derived
     stably from the URL via a short SHA-1 hash, so repeated calls are
     idempotent across operator pods).
  3. Writes the base `ValuesYaml` and the overrides YAML to two temp
     files; both are passed via `-f` so the overrides win.
  4. `helm upgrade --install {tenant}-{workload} {alias}/{chartName}`.
  5. Cleans up the temp directory.

  Release names: `{tenantId}-{workloadName}`, DNS-sanitised and truncated
  to Helm's 53-char limit.

**Hookup:** `OperatorHubService.WorkloadDeployedAsync` /
`WorkloadUndeployedAsync` now invoke the reconciler. Reconciler exceptions
are logged but **not propagated** — same rule as for tenant lifecycle
callbacks, one bad workload must not crash the hub connection.

**Docker image** (`src/CommunicationOperator/Dockerfile`) installs the
official `helm` package from `baltocdn.com` into the runtime image, and
sets `HELM_CONFIG_HOME` / `HELM_CACHE_HOME` / `HELM_DATA_HOME` under
`/operator/` so the non-root `operator-user` can write the repo cache.

**Internals visible to tests:** `InternalsVisibleTo` for the test
assembly was added so `WorkloadReconciler.ReleaseName` /
`SecretName` / `RepoAlias` (the deterministic helpers) can be asserted
directly.

Phase-3 tests:
- `Helm/HelmRunnerTests` — argument construction for repo-add (with /
  without auth), upgrade-install (files + `--set` escaping), uninstall;
  non-zero exit-code → `HelmException`.
- `Reconcilers/WorkloadReconcilerTests/DeployAsyncTests` — secret
  materialization (create / replace / cleanup-on-empty), repo
  registration with optional auth, upgrade call with correct release /
  chart-ref / values-file count.
- `Reconcilers/WorkloadReconcilerTests/UndeployAsyncTests` — `helm uninstall`
  invocation, secret-cleanup branches.
- `Reconcilers/WorkloadReconcilerTests/WorkloadOverrideYamlBuilderTests`
  — plain values, secret references, deep nesting, plaintext never
  appearing in the output for secret entries.

### Central Operator Mode (AutoManagePools)

When `OPERATOR__AUTOMANAGEPOOLS=true`, `OperatorHubService` (a `BackgroundService`) opens a SignalR connection to the Controller's `/operatorHub` and:

- on connect/reconnect, calls `RegisterOperatorAsync()` and creates pools for any tenants that already exist;
- on `TenantCreatedAsync(tenantId)`, calls `CommunicationPoolManager.CreateCommunicationPoolAsync` (creates the CR + broker secret, idempotent);
- on `TenantDeletedAsync(tenantId)`, calls `CommunicationPoolManager.DeleteCommunicationPoolAsync` (deletes both, idempotent).

The connection is auto-reconnecting via `OperatorHubClient`. Failures from the pool manager are logged but **not propagated** so that one bad tenant cannot break the hub connection.

### Webhooks

- `CommunicationPoolValidator`: rejects pool names containing spaces.
- `CommunicationPoolMutator`: currently a no-op (`NoChanges()`).

## Configuration

`OperatorOptions` is bound from the `Operator` configuration section. All keys are also available as environment variables prefixed `OPERATOR__`. See `README.md` for the full table.

Key options:

| Option | Purpose |
|--------|---------|
| `AutoManagePools` | Enables `OperatorHubService` (central mode) |
| `CommunicationControllerUri` | SignalR endpoint of the Controller (required when `AutoManagePools=true`) |
| `PoolNamespace` | Namespace where auto-created `CommunicationPool` CRs, per-tenant broker secrets, and adapter Deployments/Services live (default `octo`) |
| `DefaultPoolName` | Pool name applied to auto-created CRs |
| `BrokerHost`, `BrokerVirtualHost`, `BrokerPort` | RabbitMQ endpoint for adapter pods |
| `BrokerUser`, `BrokerPassword` | Credentials baked into `<tenantId>-<poolName>-octo-mesh-connection` secret |
| `ImagePullSecretName` | Optional pull secret added to adapter `Deployment` pod specs |
| `InstancePrefix` | Forwarded to adapter pods as `OCTO_ADAPTER__INSTANCEPREFIX` |
| `AdapterIgnoreCertificateValidation` | Forwarded to adapter pods |

## Build & Test

```bash
# DebugL = local development with monorepo NuGet packages (see /CLAUDE.md for build configurations)
dotnet build Octo.CommunicationOperator.sln -c DebugL

# Canonical: same form the Azure Pipeline runs.
# The `--` separates SDK args from Microsoft.Testing.Platform args.
dotnet test --solution Octo.CommunicationOperator.sln -c DebugL -- --report-trx --report-trx-filename test-results.trx

# Quick form during development (no TRX, no build):
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build

# Run a specific test class
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build -- \
    --treenode-filter "/*/*/CommunicationPoolValidatorTests/*"
```

### .NET 10 / Microsoft.Testing.Platform notes

Under .NET 10 SDK the legacy VSTest path is rejected (`error: Testing with VSTest target is no longer supported`). Two pieces opt this repo into the new MTP-driven `dotnet test`:

1. **`global.json`** at the repo root sets `"test": { "runner": "Microsoft.Testing.Platform" }`. Without this, `dotnet test` errors out.
2. The test csproj references `Microsoft.Testing.Extensions.TrxReport` so that `-- --report-trx --report-trx-filename ...` produces a TRX file the Azure Pipeline can publish.

Argument shape under MTP:
- The project/solution is **required** as a flag: `--project <csproj>` or `--solution <sln>`. Passing it as a positional argument is rejected (`error: Specifying a project for "dotnet test" should be done via "--project"`).
- Reporter and filter args belong **after `--`** (they're forwarded to the test executable, not the SDK).

## CI Pipeline

`devops-build/azure-pipelines.yml` builds, tests, builds the Docker image, and publishes artifacts. The structure mirrors `octo-communication-controller-services` — explicit `Restore` → `Build` → `Test` so that `OctoNugetPrivateServer` is forwarded to MSBuild on every step:

```yaml
- task: DotNetCoreCLI@2
  inputs:
    command: 'restore'
    projects: '$(solutionFile)'
    restoreArguments: '--force /p:OctoNugetPrivateServer=$(nugetPrivateServer)'
    noCache: true
- task: DotNetCoreCLI@2
  inputs:
    command: 'build'
    projects: '$(solutionFile)'
    arguments: '--no-restore --configuration $(buildConfiguration) /p:OctoNugetPrivateServer=$(nugetPrivateServer)'
- task: DotNetCoreCLI@2
  displayName: 'Test'
  inputs:
    command: 'custom'
    custom: 'test'
    arguments: '--solution $(solutionFile) --no-build --configuration $(buildConfiguration) -p:OctoNugetPrivateServer=$(nugetPrivateServer) -- --report-trx --report-trx-filename test-results.trx'
- task: PublishTestResults@2
  condition: succeededOrFailed()
  inputs:
    testResultsFormat: 'VSTest'
    testResultsFiles: '**/TestResults/test-results.trx'
```

Two reasons for this exact shape:

1. **`OctoNugetPrivateServer` must be passed to every MSBuild invocation.** `Directory.Build.props` reads it to choose `OctoVersion` (`0.1.*` from the private feed when set, `3.3.*` from nuget.org otherwise) and `RestoreSources`. If only the restore step gets it but the build/test step doesn't, MSBuild re-evaluates and falls back to nuget.org, dragging in stale transitive packages (this is how RestSharp 110.2.0 — `GHSA-4rr6-2v9v-wcpc` — slipped in earlier and tripped `NU1902` under `TreatWarningsAsErrors`).
2. **`command: 'custom'` + `--solution`** instead of `command: 'test'` + `projects: ...`. The standard form passes the project glob as positional args, which Microsoft.Testing.Platform rejects on .NET 10 SDK. `--solution` enumerates every test project in the .sln, so adding a new test project to the .sln is the only step needed to wire it into CI.

### Mandatory before commit (per repo conventions)

1. `dotnet build Octo.CommunicationOperator.sln -c DebugL` succeeds with zero warnings (`TreatWarningsAsErrors=true`).
2. The test runner above completes with all tests passing.
3. Documentation (`README.md`, this `CLAUDE.md`, `docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md`) is updated for any change in behavior or structure.

## Testing Conventions

- **Framework**: TUnit (sibling repos use `[Test]` attribute and `Assert.That(...).IsXxx(...)` fluent API). NSubstitute for mocking.
- **Project layout**: tests mirror the source folder structure (`Webhooks/`, `Services/`, `Common/`, `Finalizer/`).
- **Namespaces**: `Meshmakers.Octo.Communication.Operator.Tests.<Area>`.
- **Async exceptions on substitutes**: use `ThrowsAsync(...)` (not `Throws(...)`) for `Task`-returning members — `NS5003` is enforced as an error.
- **Disposable systems-under-test**: TUnit emits `TUnit0023` if an `IDisposable` field is not disposed. Implement `IDisposable` on the test class and dispose in `Dispose()`.
- **`OperatorOptions` injection in tests**: use `Microsoft.Extensions.Options.Options.Create(new OperatorOptions { ... })`. Fully qualify because the test file's `using Meshmakers.Octo.Communication.Operator.Options;` shadows `Options.Create`.
- **CA2252 (preview features)**: the test project sets `<EnablePreviewFeatures>true</EnablePreviewFeatures>` because KubeOps APIs are tagged `[RequiresPreviewFeatures]`. New test projects must do the same.

### Current unit-test coverage

Pure-logic + callback surfaces:

- `Common/DictionaryExtensionsTests` — label-selector formatting.
- `Webhooks/CommunicationPoolValidatorTests` — pool-name space rule.
- `Webhooks/CommunicationPoolMutatorTests` — no-op invariant.
- `Finalizer/CommunicationPoolFinalizerTests` — success result + entity passthrough.
- `Controller/CommunicationPoolControllerTests` — `ReconcileAsync` happy/failure paths and `DeletedAsync` no-status-update contract. The delete callback must not call `IKubernetesClient.UpdateStatusAsync` because the CR is already gone when KubeOps invokes it; a status-update there 404s and makes KubeOps retry the delete reconcile indefinitely.
- `Services/OperatorHubServiceTests` — `TenantCreatedAsync` / `TenantDeletedAsync` delegate to `ICommunicationPoolManager` and swallow exceptions.

Reconcilers + Kubernetes resource managers (mocked at the abstraction boundary, not against the k8s SDK):

- `Reconcilers/AdapterReconcilerTests/` — pool teardown, single-adapter teardown, reconcile flow (idempotent delete-then-recreate, image-pull-secret wiring, adapter env vars, pool-hub deployment-state callback in success and error paths). Mocks `IKubernetesClient` (KubeOps) directly — the interface is generic and ergonomic.
- `Services/CommunicationPoolManagerTests/` — auto-create/delete CR + broker secret, idempotency (no-op when already present), CR/Secret content. Mocks `ICommunicationPoolKubernetesGateway` (see below).
- `Services/OperatorHubServiceTests/ExecuteAsyncTests` — early-return paths (AutoManagePools off, controller URI missing), client creation, on-connect registration + per-tenant pool creation, clean shutdown via `IHostedService.StopAsync`. Mocks `IOperatorHubClientFactory` to substitute the SignalR client (see below).

### `IOperatorHubClientFactory` — the seam for `OperatorHubClient`

`OperatorHubService.ExecuteAsync` originally `new`'d an `OperatorHubClient` directly, which made the SignalR connection logic untestable. The factory interface produces an `IOperatorHubClient` (already exposed by the SDK) and is mocked in tests. Production wiring lives in `OperatorHubClientFactory` (registered as singleton in `Program.cs`).

Tests use `client.When(c => c.EnableReconnect(...)).Do(_ => tcs.TrySetResult())` as a sync point — once `EnableReconnect` has been called, the connect callback has already finished and the service is parked in `Task.Delay(Infinite, stoppingToken)`. Asserting before that yields race conditions where the assertion runs before `ExecuteAsync` reaches the verified line.

### `ICommunicationPoolKubernetesGateway` — the seam for `IKubernetes`

`CommunicationPoolManager` originally talked directly to `IKubernetes` and used a stack of extension methods (`CustomObjects.GetNamespacedCustomObjectAsync`, `CoreV1.ReadNamespacedSecretAsync`, …). Mocking that surface is verbose because:
- the extensions delegate to nested sub-interfaces (`ICustomObjectsOperations`, `ICoreV1Operations`),
- the `Exists`-via-404 idiom requires throwing `HttpOperationException` with a fake `HttpResponseMessageWrapper`,
- assertions then have to target the underlying `*WithHttpMessagesAsync` method names rather than the readable extension API.

The `ICommunicationPoolKubernetesGateway` interface (in `Services/`) collapses that surface to six methods: `CommunicationPoolExistsAsync`, `CreateCommunicationPoolAsync`, `DeleteCommunicationPoolAsync`, `SecretExistsAsync`, `CreateSecretAsync`, `DeleteSecretAsync`. The implementation `CommunicationPoolKubernetesGateway` keeps every k8s-SDK quirk (404 → `false`, extension-method routing, CRD group/version/plural constants) in one place. Add new k8s calls to the interface — don't reach back into `IKubernetes` from elsewhere.

### Not yet covered

- `CommunicationPoolKubernetesGateway` itself — would need either an integration test against a real (or fake) k8s API or low-level `IKubernetes` mocking. Treated as a thin pass-through layer; covered indirectly by E2E tests.
- `OperatorHubClientFactory` — the production `new OperatorHubClient(...)` wrapper; same rationale as above.

## Code Quality Standards

- `Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latestmajor` (inherited from `Directory.Build.props`).
- `EnablePreviewFeatures=true` on both the operator project and the test project (KubeOps annotations).
- Target framework: `net10.0`.
- Three build configurations: `Debug`, `Release`, `DebugL` — DebugL pulls Octo NuGet packages from the monorepo's `../nuget/` cache. Both `CommunicationOperator.csproj` and `CommunicationOperator.Tests.csproj` declare all three configurations; the .sln maps each one straight through.

## E2E Smoke Test

Manual end-to-end validation of the central-operator path lives at
`docs/E2E-SMOKE-TEST.md`. It uses the `octo-tools` PowerShell modules
(`Install-OctoKubernetes`, `Start-Octo`) plus `start-operator.ps1` at the
repo root to bring up the full stack (Mongo, RabbitMQ, kind, all backend
services, operator) and triggers the lifecycle via `octo-cli`. Run it after
non-trivial changes in `OperatorHubService` or `CommunicationPoolManager`.

`start-operator.ps1` is intentionally **not** named `octo-start.ps1` — that
would cause `Start-Octo` to launch the operator automatically, which we want
to keep opt-in for now.

## Related Documentation

- `docs/E2E-SMOKE-TEST.md` — manual smoke-test runbook (see above).
- `docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md` — long-form design notes for application deployment management and version lifecycle (Helm-only deployment, `ManuallyDeployed` state).
- Monorepo root `CLAUDE.md` — global build configurations, multi-tenancy, naming conventions.
- `octo-communication-controller-services/CLAUDE.md` — counterpart on the controller side; the SignalR `/operatorHub` contract lives there.
