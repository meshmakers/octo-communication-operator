# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

The **Octo Communication Operator** is a Kubernetes operator that manages mesh adapter deployments via the [KubeOps](https://github.com/buehler/dotnet-operator-sdk) framework. It watches `CommunicationPool` custom resources and, optionally, connects to the Communication Controller via SignalR to receive tenant lifecycle events and auto-create/auto-delete `CommunicationPool` CRs.

It supports two deployment modes:

- **Edge deployment**: the operator runs on a remote edge cluster. `CommunicationPool` CRs are managed manually (or by an external system); the operator only reconciles existing CRs into adapter deployments and services. When multiple operator instances share one edge cluster (one per target controller), each must set `OPERATOR__WATCHNAMESPACE` so they only reconcile CRs in their own namespace and don't race on each other's resources.
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
│   ├── Reconcilers/   WorkloadReconciler (Helm-based deploy for Adapters + Applications)
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
3. The Controller fans out a `WorkloadDeployedAsync` event on `/operatorHub` for each Adapter and Application managed by the pool.
4. `WorkloadReconciler` materializes any secret-flagged values into an operator-owned `Secret`, registers the chart repository and runs `helm upgrade --install` per workload (see [Helm Workload Reconciliation](#helm-workload-reconciliation) below).
5. On pool deletion, the operator receives matching `WorkloadUndeployedAsync` events and runs `helm uninstall` for every release.

There is no longer a raw-K8s `AdapterReconciler` path; Adapters and Applications are deployed exclusively via Helm releases.

`PoolService.UnRegisterPoolAsync` (called from `CommunicationPoolController.DeletedAsync`) treats any `HubException` from the controller-side `UnregisterPoolOperatorAsync` call as a **soft failure** and only logs it. Reason: the CR is already gone when `DeletedAsync` fires, and during the tenant-delete cascade the tenant itself no longer exists at the controller — so the unregister roundtrip will respond with `TenantException`. Re-throwing would put the entity back in the KubeOps retry queue forever. The local connection is still stopped and the pool removed from `_pools` regardless.

### Helm Workload Reconciliation

Workloads (Adapters + Applications) deployed to a Cloud pool are driven by
the `WorkloadReconciler` over the `helm` CLI.

**Layers:**
- `Helm/IHelmProcessInvoker` + `HelmProcessInvoker` — low-level
  `System.Diagnostics.Process` wrapper around the `helm` binary on PATH.
  Captures stdout / stderr, masks `--username` / `--password` values from
  the debug log line.
- `Helm/IHelmRunner` + `HelmRunner` — high-level operations:
  `EnsureRepoAsync` (idempotent `helm repo add --force-update` + `helm repo update`),
  `UpgradeInstallAsync` (with `-f`, `--set`, `--atomic`),
  `UpgradeInstallDryRunAsync` (same args minus `--atomic`, plus
  `--dry-run=server` — see [Pre-flight + Diagnostics](#pre-flight--diagnostics) below),
  `UninstallAsync` (uses `--ignore-not-found`). Non-zero exit codes become
  `HelmException` with full stderr.
  - **Empty / whitespace `version`**: `UpgradeInstallAsync` omits the `--version`
    argument entirely when the value is blank, so helm picks the newest tag in
    the configured repo. This is the contract for
    `System.Communication.MainLatest` on dev/test clusters — the blueprint
    seeds an empty `ChartVersion` and the CD pipeline writes a concrete
    `0.1.<yyMMDDxxx>` later. Pass a non-blank value to pin a specific chart.
  - `GetInstalledChartVersionAsync` (`helm list --filter ^{release}$ -o json`)
    reads the chart version of the release as it currently stands. Helm reports
    `{chartName}-{version}` and chart names routinely contain dashes
    (`octo-mesh-adapter`), so the version is split off against the known chart
    name and the method returns `null` — rather than a guess — when the prefix
    does not match. `helm list` rather than `helm history` on purpose: the
    newest history entry may be a failed or still-pending attempt, and the
    question here is what is *running*.

### Reconciliation Keeps the Installed Chart Version (AB#4955)

An empty `ChartVersion` means "newest in the repository", resolved by helm at
`helm upgrade` time. On a deploy a human triggered that is the request. But the
controller also re-dispatches stranded `Pending` workloads on every pool
re-registration (AB#4894) — which happens on operator restarts, blueprint
re-applies, CK-model updates and `EnableCommunication`. Resolving anew there
moved six prod-1 accounting workloads from chart 1.0.71 to 1.0.72 with nobody
deploying them, and the new version happened to carry a defect (AB#4951), which
is the only reason it was noticed.

`WorkloadDeployedDto.IsReconciliation` (SDK contract) marks a dispatch as
"restore what was supposed to be running" rather than a release decision.
`WorkloadReconciler.ResolveChartVersionAsync` decides accordingly:

| `IsReconciliation` | `ChartVersion` | Version used |
|---|---|---|
| false (user deploy) | empty | newest in the repository — unchanged, this is what `System.Communication.MainLatest` depends on |
| false | pinned | the pin |
| true | pinned | the pin (helm is not even asked) |
| true | empty | the version of the **installed release**, read back via `GetInstalledChartVersionAsync` |
| true | empty, nothing installed | newest — a reconcile for a release that was never installed is a first install |

The resolved version feeds both the `--dry-run=server` pre-flight and the real
install, so the pre-flight validates the chart that actually gets applied. The
lookup is best effort: a helm failure is logged and the deploy proceeds with the
workload's own (empty) version, because recovering the stranded workload matters
more than pinning it.

The flag is additive and defaults to false, so an operator that pre-dates it
behaves exactly as before, and a controller that pre-dates it never sets it.
That mixed-fleet window is why the controller still writes a warning event on
every unpinned re-dispatch.

Tests: `Reconcilers/WorkloadReconcilerTests/ReconcileChartVersionTests` (all six
rows of the table above plus the pre-flight pinning) and the
`GetInstalledChartVersionAsync` parsing cases in `Helm/HelmRunnerTests`.
- `Reconcilers/WorkloadContextValuesBuilder` — turns the operator's own
  `OperatorOptions` (cluster-internal Mongo/RabbitMQ/CrateDB hosts,
  reporting service URI, instance prefix, ingress defaults) plus
  workload identity from `WorkloadDeployedDto` (`tenantId`,
  `adapterRtId` from `WorkloadRtId`) into a `values-context.yaml` file.
  Every field is optional: only those that are set get projected, so an
  edge operator with an empty DTO context (which should not happen in
  production) passes no context layer at all. Secrets are deliberately
  **not** handled here — they flow through `WorkloadOverrideYamlBuilder`
  and the per-release secret.
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
  3. Writes up to three values files to a temp dir and passes them via
     `-f` in this order — Helm later-args win, so order = precedence:
     - `values-context.yaml` (operator-managed cluster defaults; lowest)
     - `values-base.yaml` (the workload's own `ValuesYaml` from the CK
        entity)
     - `values-overrides.yaml` (structured per-value overrides from the
        Studio form; highest)
  4. `helm upgrade --install {tenant}-{workload} {alias}/{chartName}`.
  5. Cleans up the temp directory.

  Release names: `{tenantId}-{workloadName}`, DNS-sanitised and truncated
  to Helm's 53-char limit.

  Before the override builder runs, the reconciler also calls
  `AppendClusterSecrets`. Three tiers:

  1. **`secrets.rabbitmq`** (from `BrokerPassword`) — injected
     **unconditionally** whenever `BrokerPassword` is set. RabbitMQ is
     the controller↔adapter command bus; every adapter needs it
     regardless of whether it also touches data stores. Lumping this
     into the cluster-secrets gate previously made pure edge adapters
     (Modbus / Loxone) fail the chart's mandatory `secrets.rabbitmq`
     check even though they have no business with Mongo or CrateDB.

  2. **`secrets.rootCa`** (from `RootCaCertificate`, AB#4417) — injected
     **unconditionally** whenever `RootCaCertificate` is set, same gate
     as `BrokerPassword`. On clusters whose ingress/controller endpoint
     uses a private CA (e.g. the local kind getting-started quickstart),
     the operator pod itself trusts the CA via the chart's
     `secrets.rootCa` value, but a workload with
     `ReceivesClusterSecrets=false` (e.g. the simulation adapter) still
     opens a TLS connection to the Communication Controller and needs
     the same trust anchor or the handshake fails and the workload never
     registers. Unlike every other entry here, this one is **not**
     secret-flagged (`IsSecret = false`) — the workload chart's own
     `secrets.rootCa` handling (`templates/secret.yaml` +
     `templates/deployment.yaml`, mirroring the operator chart's own
     trust-splice init container) `b64enc`s `.Values.secrets.rootCa`
     directly and requires a plain string; a `valueFrom.secretKeyRef` map
     there would break chart rendering. `RootCaCertificate` itself
     reaches the operator process the same way `BrokerPassword` does — a
     `secretKeyRef`-backed environment variable (`OPERATOR__ROOTCACERTIFICATE`)
     sourced from the operator chart's own `{fullname}-ca` Secret (the
     same Secret that already backs the operator's own trust-splice init
     container).

  3. **Data-store secrets** (`secrets.databaseUser`,
     `secrets.databaseAdmin`, `secrets.streamDataPassword` from
     `ClusterSecrets.*`) — only injected when the workload's
     `WorkloadDeployedDto.ReceivesClusterSecrets` flag is true (set by
     the controller from the Adapter CK entity's
     `ReceivesClusterSecrets` attribute). Pure edge adapters should
     leave this flag false; the chart's own `features.mongo` /
     `features.streamData` gates then skip emitting the matching env
     blocks entirely (see `octo-plug-modbus`, `octo-adapter-loxone`).

  Injected entries are prepended so any entity-supplied override on the
  same path still wins. Secret-flagged values then flow through the
  normal secret-flagged pipeline: materialised into
  `{release}-octo-secrets`, referenced from the chart via
  `valueFrom.secretKeyRef`. Each adapter chart's `secrets.*` block must
  accept both plaintext strings (legacy) and `valueFrom` maps for this
  contract to work; see `octo-mesh-adapter` / `octo-eda-adapter` chart
  `templates/_helpers.tpl` (`octo-mesh.secretEnv`). `secrets.rootCa` is
  the one exception: it is never secret-flagged, so it always renders as
  a plain literal in `values-overrides.yaml`.

### Stale Helm-Lock Recovery (AB#4894)

A helm process killed mid-upgrade — e.g. the operator pod replaced by a rollout while a deploy
was in flight — leaves the release's newest revision in a `pending-*` status. That lock blocks
every later install/upgrade/rollback with "another operation is in progress" and never clears
itself; the only remedy used to be a manual Undeploy→Deploy cycle (observed live on
prod-1/energyiq, 2026-08-26). Before the pre-flight, `WorkloadReconciler.DeployAsync` calls
`TryClearStaleHelmLockAsync`:

1. `IHelmRunner.GetLatestReleaseRevisionAsync` (`helm history {release} -o json --max 1`;
   `null` when the release does not exist).
2. Only when the newest revision `IsPending`: read the creation timestamp of the release
   secret `sh.helm.release.v1.{release}.v{rev}` via
   `ICommunicationPoolKubernetesGateway.GetSecretCreationTimestampAsync`.
3. Only when the secret is older than `WorkloadReconciler.StaleHelmLockThreshold` (default
   10 min — comfortably above helm's 5-min atomic timeout, so a live run on the outgoing pod
   of a rolling operator upgrade is never robbed of its lock): delete the secret and log a
   warning. Everything is best effort — any failure logs and lets the deploy proceed (and fail
   on the lock exactly as before).

Tests: `Reconcilers/WorkloadReconcilerTests/StaleHelmLockTests` (stale lock cleared before the
dry-run, fresh pending lock untouched, healthy release never inspected, history failure never
blocks the deploy) and the history/parse cases in `Helm/HelmRunnerTests`.

### Pre-flight + Diagnostics

`helm upgrade --install --atomic` collapses every failure into one
opaque stderr line — typically `Error: release X failed, and has been
uninstalled due to atomic being set: context deadline exceeded`. The
actual root cause (`ImagePullBackOff`, admission-webhook denial,
`CrashLoopBackOff`, missing secret, …) is observable on the cluster
while helm waits, but `--atomic` rolls everything back before the
caller sees the failure. Two layers wrap the real install to surface
the actual reason:

1. **Pre-flight via `--dry-run=server`** (`UpgradeInstallDryRunAsync`,
   called before the real install in `WorkloadReconciler.DeployAsync`).
   Helm renders the manifests and submits them to the apiserver with
   `dryRun=All` — admission webhooks, OpenAPI schema validation and
   RBAC all run, but no resources are created. Catches schema errors,
   Gatekeeper/Kyverno rejections, RBAC issues and invalid
   annotations in &lt;2s instead of letting the real install burn the
   full atomic timeout. Throws `HelmException` with operation tag
   `upgrade --install --dry-run=server {release}` on failure; the real
   install is then skipped entirely.

2. **Post-failure diagnostics** (`Diagnostics/IWorkloadDiagnosticsCollector`).
   When the real install throws `HelmException`, the reconciler calls
   `CollectAsync(namespace, release)`:
   - Lists pods labeled `app.kubernetes.io/instance={release}` and
     scrapes `ContainerStatuses[*]` / `InitContainerStatuses[*]` for
     non-benign `Waiting.Reason` (everything except `PodInitializing`
     / `ContainerCreating`) and non-zero `LastState.Terminated.ExitCode`.
   - Lists namespace events with `type=Warning` and keeps the ones
     whose `InvolvedObject.Name` starts with the release name (covers
     Deployment / ReplicaSet / Pod / Service / Ingress that helm names
     from the release).

   Both calls are wrapped individually — a failure in one (e.g. pods
   already gone because atomic rollback finished, or RBAC denial)
   doesn't suppress the other. Events outlive pods (default TTL 1h),
   so the post-failure path reliably catches `ImagePull` /
   `FailedScheduling` / `FailedMount` even when atomic has wiped the
   pods. The diagnostic call itself is bounded by a 10s
   `CancellationTokenSource` so a stuck apiserver can't hang the
   failure path. When the collector returns a non-empty string the
   reconciler throws a new `HelmException` with stderr
   `{original-stderr}\n\nPod diagnostics:\n{collected}`. Empty result →
   original exception rethrown unchanged.

   The internal formatters `FormatPodStates` /
   `FormatWarningEvents` are exposed via `InternalsVisibleTo` so the
   tests don't have to mock the verbose `IKubernetes` /
   `ICoreV1Operations` surface — the thin glue methods
   `ListPodsAsync` / `ListWarningEventsAsync` are pass-throughs and
   exercised manually / via E2E.

**Hookup:** `OperatorHubService.WorkloadDeployedAsync` /
`WorkloadUndeployedAsync` invoke the reconciler. Reconciler exceptions
are logged but **not propagated** — same rule as for tenant lifecycle
callbacks, one bad workload must not crash the hub connection.

### Live Deploy Watcher

The pre-flight / post-failure pair above still leaves a 5-minute gap
between "something is wrong" and "user sees it": helm waits its full
`--timeout` (default 5 min) before the post-failure collector runs.
`Reconcilers/WorkloadDeployWatcher` closes that gap.

Started by `WorkloadReconciler.DeployAsync` right before the real
`helm upgrade --install --atomic`, the watcher loop:

1. Sleeps `DefaultPollInterval` (3 s) — overridable per call so tests
   can drive the loop at millisecond speeds.
2. Calls `IWorkloadDiagnosticsCollector.CollectAsync` (same collector
   the post-failure path uses) with a 5 s `CollectTimeout`.
3. If the snapshot is non-empty AND different from the last one sent,
   pushes it through
   `IOperatorHubInvoker.ReportWorkloadDeploymentProgressAsync` →
   controller-side `OperatorHub.ReportWorkloadDeploymentProgressAsync`
   → `Set{Adapter,Application}DeploymentStateAsync(Pending, message)`.
   `DeploymentState` deliberately stays at `Pending` — helm may still
   recover (e.g. registry blip), so the terminal state machine remains
   owned by `ReportWorkloadDeploymentStatusAsync`.
4. Collector / hub exceptions are caught and logged at debug; the loop
   continues so a transient apiserver glitch can't silently disable
   feedback for the rest of the deploy.
5. Cancels and returns when its `CancellationToken` fires. The
   reconciler cancels + awaits the watcher in its `finally` before
   `OperatorHubService.WorkloadDeployedAsync` writes the terminal
   status — SignalR preserves message order on a single connection so
   the terminal write always arrives after the last progress write.

Backward compat: older controller builds reject the new hub method
with `HubException`. `OperatorHubService.ReportWorkloadDeploymentProgressAsync`
catches that, logs a single warning (`Interlocked.CompareExchange` on
`_progressUnsupportedLogged`) and degrades silently — every 3-second
tick would otherwise spam the log.

### Cancellable Deploy

`WorkloadReconciler._inFlightDeploys` (`ConcurrentDictionary<release,
CancellationTokenSource>`) tracks every running `DeployAsync`. The CTS
is linked to the incoming token, so upstream shutdown still cancels —
but it's also reachable from `UndeployAsync` via this dict.

`UndeployAsync` checks the dict first:
- If a deploy is in flight for the same release: cancel its CTS, wait
  `CancelGracePeriod` (2 s) for helm's atomic rollback to settle, then
  run `helm uninstall --ignore-not-found` as usual. The grace window
  matters because `helm uninstall` racing with the in-flight atomic
  rollback would either deadlock on the release lock or leave the
  release in a `failed` state that takes a second uninstall to clear.
- If no deploy is in flight: existing path unchanged.

Cancellation only works end-to-end because `HelmProcessInvoker.InvokeAsync`
explicitly `process.Kill(entireProcessTree: true)` on
`OperationCanceledException`. `WaitForExitAsync(ct)` alone throws but
leaves the helm process running, holding the release lock — kubectl
and registry-handshake helpers are forked as children, hence the
whole-tree kill.

Concurrent deploys for the same release throw
`InvalidOperationException` from the `_inFlightDeploys.TryAdd` guard.
The controller's pool-service path is serial per workload so this
should not happen in practice; the explicit guard turns "what if" into
a controlled failure with an actionable message.

**Docker image** (`src/CommunicationOperator/Dockerfile`) downloads the
official `helm` binary tarball from `get.helm.sh` (CNAME for the helm
GitHub Releases) — the previous baltocdn.com apt-repo path was blocked
on the `meshmakers-ci-agents` pool. Version is pinned via the
`HELM_VERSION` build-arg (default `v3.16.4`); multi-arch builds work
because `TARGETARCH` is forwarded by Buildx. `HELM_CONFIG_HOME` /
`HELM_CACHE_HOME` / `HELM_DATA_HOME` are set under `/operator/` so the
non-root `operator-user` can write the repo cache.

**Internals visible to tests:** `InternalsVisibleTo` for the test
assembly was added so `WorkloadReconciler.ReleaseName` /
`SecretName` / `RepoAlias` (the deterministic helpers) can be asserted
directly.

**Shared k8s-name sanitiser** (`Common/K8sNaming`): both the workload
reconciler and the `CommunicationPoolManager` derive Kubernetes resource
names / label values from CK entity attributes (tenantId, poolName,
workloadName) that may contain whitespace, uppercase letters, or other
characters the apiserver rejects with a 422 (e.g. a pool literally
named `"Communication Pool"` produced
`sbeg-communication pool-octo-mesh-connection`, which fails RFC 1123).
`K8sNaming.DnsName` returns a strict subdomain segment (lowercase,
`[a-z0-9-]`, dashes collapsed, capped at 53 chars by default for
parity with Helm's release-name limit). `K8sNaming.LabelValue` keeps
the laxer label alphabet (also allows `_` and `.`, returns `"unknown"`
for empty input, capped at 63). `WorkloadReconciler.ReleaseName` /
`SanitizeLabelValue` are now thin delegates so both call sites stay in
lockstep; the original CK pool/workload name is preserved on every
generated resource as the
`octo-mesh.meshmakers.io/pool-name` /
`octo-mesh.meshmakers.io/workload-name` annotation.

Tests:
- `Helm/HelmRunnerTests` — argument construction for repo-add (with /
  without auth), upgrade-install (files + `--set` escaping),
  upgrade-install dry-run (`--dry-run=server` present, `--atomic`
  absent, operation tag flags pre-flight failures), uninstall;
  non-zero exit-code → `HelmException`.
- `Diagnostics/WorkloadDiagnosticsCollectorTests` — pure-formatter
  tests over `FormatPodStates` / `FormatWarningEvents`: ImagePullBackOff
  surfaced, benign waiting states (`ContainerCreating` /
  `PodInitializing`) suppressed, init-container failures tagged as
  `initContainer`, terminated exit codes reported, unrelated events
  excluded, duplicates deduplicated.
- `Reconcilers/WorkloadReconcilerTests/DeployAsyncTests` — secret
  materialization (create / replace / cleanup-on-empty), repo
  registration with optional auth, upgrade call with correct release /
  chart-ref / values-file count; dry-run runs **before** real install
  (`Received.InOrder`); dry-run failure skips real install entirely;
  real-install failure invokes diagnostics; collector output is merged
  into the rethrown `HelmException.StdErr`; empty diagnostics → original
  exception rethrown unchanged.
- `Reconcilers/WorkloadReconcilerTests/UndeployAsyncTests` — `helm uninstall`
  invocation, secret-cleanup branches.
- `Reconcilers/WorkloadReconcilerTests/WorkloadOverrideYamlBuilderTests`
  — plain values, secret references, deep nesting, plaintext never
  appearing in the output for secret entries.
- `Reconcilers/WorkloadReconcilerTests/WorkloadContextValuesBuilderTests`
  — empty options → null, partial options → only set keys emitted, full
  options → complete YAML with cluster dependencies + ingress
  annotations. `DeployAsyncTests` also asserts file ordering (context →
  base → overrides) when operator context is set.

### OperatorHubService Lifecycle (Central + Edge)

`OperatorHubService` (a `BackgroundService`) opens a SignalR connection to the Controller's `/operatorHub` **whenever `OPERATOR__COMMUNICATIONCONTROLLERURI` is configured** — required in both central and edge modes. Without this connection the operator's `IOperatorHubInvoker.RegisterPoolAsync` no-ops, and pools registered through `CommunicationPoolController.ReconcileAsync` never reach the controller (the entity stays at `Unregistered` in the Studio UI). The previous early-return on `!AutoManagePools` was the cause of the regression where edge-cluster pools showed up as Unregistered indefinitely.

`OPERATOR__AUTOMANAGEPOOLS` is now a narrower flag — it only gates the **side effect of auto-creating / -deleting `CommunicationPool` CRs** in response to controller broadcasts:

- `AutoManagePools=true` (central): `PoolDeployedAsync` → `CommunicationPoolManager.CreateCommunicationPoolAsync` (creates the CR + broker secret, idempotent). `PoolUndeployedAsync` → `DeleteCommunicationPoolAsync`. `RegisterOperatorAsync()` on (re)connect also fans out `CreatePoolAsync` for every already-deployed pool.
- `AutoManagePools=false` (edge): `PoolDeployedAsync` / `PoolUndeployedAsync` log + return without touching `ICommunicationPoolManager`, **and** the `RegisterOperatorAsync()` reconnect fan-out is gated by the same flag. The latter gate is load-bearing: without it, every edge-operator pod restart would materialize a CR + broker secret for every Cloud pool the controller knows about, and the operator would then `RegisterPoolAsync` them — putting workload-deploy events on a route that also lands on the edge cluster. CRs on the edge cluster are managed manually or by an external system.

Either way, the workload-deploy path (`WorkloadDeployedAsync` → `WorkloadReconciler.DeployAsync`) and the pool register/unregister round-trip from `CommunicationPoolController.ReconcileAsync` go through the same SignalR client.

The connection is auto-reconnecting via `OperatorHubClient`. Failures from the pool manager and workload reconciler are logged but **not propagated** so that one bad event cannot break the hub connection.

### Pool-Registration Retry Loop (AB#4371)

A pool registration the **controller rejects while the SignalR connection
stays alive** used to be logged and forgotten: the reconnect callback is the
only re-registration trigger, and it only fires when the connection drops.
Observed on prod-1: all pods restarted together, the operator reconnected
while the controller's CkCache was still importing tenant models,
`RegisterPoolAsync` threw `CommunicationRepositoryException` once — and the
pool stayed orphaned until the next pod restart. The controller then dropped
every workload deploy/undeploy for that pool ("No operator currently owns
pool ...", queued controller-side since AB#4371).

`OperatorHubService.RetryPoolRegistrationLoopAsync` closes the gap:

- Started once in `ExecuteAsync`, runs for the service lifetime, cadence
  `OperatorOptions.PoolRegistrationRetrySeconds` (default 30, fractional
  values allowed for tests, `<= 0` disables with a warning).
- Each tick (only while `client.IsAlive`): registers every owned pool with
  `IsRegistered == false`, flips the flag on success, and fires the per-pool
  reverse-sync (`ReportDeployedPoolAsync`) so a drifted `DeploymentState` is
  restored. Failures are logged and retried on the next tick.
- The reconnect callback now calls `PoolService.ResetRegistrationState()`
  **before** replaying registrations — a pool registered on a previous
  connection that fails re-registration would otherwise keep a stale
  `IsRegistered=true` and be invisible to the retry loop.

Registration is idempotent on the controller (`RegisterPoolForConnection`
is a set-add; the state write is guarded), so a retry racing a reconnect
replay is harmless.

Tests: `Services/OperatorHubServiceTests/RegistrationRetryTests` —
rejected-on-connect recovers via retry, recovery fires the per-pool
reverse-sync, connect callback resets registration state, `<= 0` disables
the loop.

### Workload Scale Verb (AB#4917 — On-Demand Lifecycle AB#4914)

The controller's on-demand lifecycle (scale-to-zero for idle adapters) drives replica
changes through a dedicated hub callback instead of helm:

- `IOperatorHubCallbacks.ScaleWorkloadAsync(ScaleWorkloadDto)` →
  `OperatorHubService.ScaleWorkloadAsync` → `WorkloadReconciler.ScaleAsync` →
  `ICommunicationPoolKubernetesGateway.ScaleDeploymentsByInstanceAsync`. The gateway lists
  Deployments by the `app.kubernetes.io/instance={release}` label (never derives resource
  names — Application charts may render `{release}-{chart}`) and merge-patches
  `{"spec":{"replicas":N}}` on each. A plain Deployment patch, not the scale subresource,
  so it runs under the operator's existing `apps/deployments: ['*']` RBAC. No helm run,
  no release-history churn; a scale completes in ~2 s.
- The outcome is reported via `IOperatorHub.ReportWorkloadScaleStatusAsync`
  (`WorkloadScaleStatusDto`; `Success=false` when the release has no Deployments or a
  patch failed). The controller uses the ack to advance its lifecycle state machine
  (`Draining → Hibernated` on a scale-0 ack). Older controller builds reject the method —
  `OperatorHubService` logs one warning (`_scaleStatusUnsupportedLogged` latch, same
  pattern as the deploy-progress channel) and degrades silently.
- **Redeploy must not resurrect a hibernated workload:** when
  `WorkloadDeployedDto.Hibernated` is true, `WorkloadReconciler.DeployAsync` adds
  `--set replicaCount=0` (applies to both the dry-run pre-flight and the real install);
  `--set` beats every `-f` values layer. A deploy that is supposed to wake the workload
  goes through the controller's wake gate first, which clears the hibernated state before
  the deploy event is sent.
- Reconciler/scale failures follow the existing rule: logged, reported in the ack, never
  propagated into the hub connection.

Tests: `Reconcilers/WorkloadReconcilerTests/ScaleAsyncTests`, the hibernation-pin cases in
`DeployAsyncTests`, and `Services/OperatorHubServiceTests/ScaleWorkloadTests`.

### Reverse-Sync on Reconnect

After the operator has re-registered every owned `CommunicationPool` CR
with the controller (the `RegisterPoolAsync` loop in `onReconnect`), a
**Cloud operator** (`AutoManagePools=true`) follows up with one call to
`IOperatorHub.ReportDeployedStateAsync(reports)` carrying the set of
pools it currently has CRs for. The controller restores
`DeploymentState=Deployed` on any pool whose state drifted while the
operator was offline (e.g. controller restart between deploys lost the
in-memory `OperatorConnectionManager` tracking) and rebuilds the
per-connection pool registration so undeploy fan-out keeps working.

**Two coupled paths run the reverse-sync:**

1. **Bulk on reconnect** (`OperatorHubService.onReconnect`): captures
   the snapshot of `poolService.GetPools()` when the SignalR connect
   callback fires and sends them all in one call. Works for the
   *controller-restart* case where the operator's KubeOps cache was
   never torn down — every CR is in `_pools` by the time the callback
   runs.
2. **Per-pool on register** (`PoolService.RegisterPoolAsync` →
   `IOperatorHubInvoker.ReportDeployedPoolAsync`): every CR reconcile
   that registers a pool also fires a single-pool reverse-sync. Closes
   the *operator-restart* race where KubeOps populates `_pools`
   AFTER the bulk callback already ran: CRs discovered later than the
   snapshot would otherwise miss their restore window and stay stuck
   at whatever drifted state the controller had on them. Per-pool is
   idempotent on the controller side (restore-only-when-changed) so the
   double coverage doesn't spam audit events.

Gating:

- `AutoManagePools=false` (edge): the operator skips the call entirely.
  The controller-side handler rejects edge operators with a typed
  `HubException` anyway — skipping at the source avoids an avoidable
  error audit event on every reconnect.
- Owned-pool list empty (fresh install): skip the call. Sending an
  empty report is a valid no-op on the controller but adds round-trip
  cost and log noise.
- Call failure (e.g. controller on an older build that doesn't know
  the contract): logged at warning, **not propagated**. Self-healing is
  best-effort — the next deploy/undeploy event will write the correct
  state regardless.

Workloads are **not yet covered** by the reverse-sync: the operator has
no persistent helm-release-to-workload-rtId mapping that survives a pod
restart, so each pool report ships with an empty `WorkloadRtIds[]`. The
controller-side restore handles empty lists cleanly. Future work: track
workload rtIds via a label on the helm release secret (helm 3.13+
`--labels`) or on the operator-owned `{release}-octo-secrets` Secret so
the operator can read them back at startup. See
`docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md` for the contract details.

Tests:
- `Services/OperatorHubServiceTests/ReverseSyncTests` — Cloud with owned
  pools sends report, edge does NOT call, empty owned-pool list skips
  the call, `ReportDeployedStateAsync` failure is logged but doesn't
  crash the connect callback.

### Webhooks

- `CommunicationPoolValidator`: requires `Spec.PoolRtId` to be a
  24-character lowercase hex MongoDB ObjectId (the RtId of the
  controller-side `RtPool`). `Spec.PoolName` is optional — the rtId
  is the canonical pool identity, and the human-readable display name
  lives on the controller's `RtPool.Name` attribute. Every derived
  k8s name is built from `PoolRtId` via `K8sNaming.DnsName`. An empty
  / malformed `PoolRtId` would otherwise surface only as a hub-side
  `FormatException` from the controller's `OperatorHub.RegisterPoolAsync`
  and leave the CR stuck Unregistered.
- `CommunicationPoolMutator`: currently a no-op (`NoChanges()`).

## Configuration

`OperatorOptions` is bound from the `Operator` configuration section. All keys are also available as environment variables prefixed `OPERATOR__`. See `README.md` for the full table.

Key options:

| Option | Purpose |
|--------|---------|
| `AutoManagePools` | Enables auto-creating / -deleting `CommunicationPool` CRs in response to `PoolDeployedAsync` / `PoolUndeployedAsync` broadcasts from the controller. Central operator only. Edge operators leave this `false` — the SignalR connection itself runs in both modes (gated by `CommunicationControllerUri`), only the CR-management side effect is toggled. |
| `WatchNamespace` | Restricts the CR watcher to a single namespace. When null/empty (default), the operator watches all namespaces cluster-wide. Required when running multiple operator instances on the same cluster (e.g. one per target controller on an edge device) so they don't race on the same CRs. Wired via `KubeOps.Abstractions.Builder.OperatorSettingsBuilder.WithNamespace()`. |
| `CommunicationControllerUri` | SignalR endpoint of the Controller. Required in **both** central and edge modes for `OperatorHubService` to start. When empty, the hub service logs a warning and exits, and `IOperatorHubInvoker.RegisterPoolAsync` becomes a no-op (CR-reconcile finishes locally but the controller never sees the pool). |
| `WorkloadCommunicationControllerUri` | Controller URI projected into every deployed workload's Helm values. Empty (default) projects `CommunicationControllerUri` — one address serves the operator's own hub connection and the workloads, correct wherever both resolve it the same way. Set it when the operator needs an address the workloads cannot use (local kind: host-run controller reachable for the operator via a pod hostAlias only — the adapters then sat at Unregistered while the operator looked healthy, AB#4967). |
| `PoolRegistrationRetrySeconds` | Cadence of the pool-registration retry loop (see "Pool-Registration Retry Loop" above). Default 30; fractional values allowed; `<= 0` disables the loop. |
| `PoolNamespace` | Namespace where auto-created `CommunicationPool` CRs and per-tenant broker secrets live (default `octo`). Helm releases are deployed into the same namespace unless the chart's values override it. |
| `DefaultPoolName` | Pool name applied to auto-created CRs |
| `BrokerHost`, `BrokerVirtualHost`, `BrokerPort` | RabbitMQ endpoint for adapter/application pods |
| `BrokerUser`, `BrokerPassword` | Credentials baked into `<tenantId>-<poolName>-octo-mesh-connection` secret consumed by the Helm charts |
| `InstancePrefix` | Forwarded to workload pods via the Helm chart values |
| `AdapterIgnoreCertificateValidation` | Forwarded to workload pods via the Helm chart values |
| `ReportingServiceUri` | Cluster-internal URI of the reporting service. When set, projected into each workload's Helm values as `reportingServiceUri`. |
| `AuthUri` | Public URI of the identity service issuing the access tokens secured trigger nodes accept. When set, projected into each workload's Helm values as `authUri`. Must be the public issuer address rather than a cluster-internal service name — the adapter uses it as the expected issuer of the token, not merely as an address to fetch signing keys from. Empty values are deliberately not emitted: the adapter chart would render `OCTO_ADAPTER__AUTHORITYURL` as an empty string, overriding the default compiled into the adapter. |
| `ClusterDependencies.MongodbHost` / `MongodbReplicaSet` / `RabbitMqHost` / `RabbitMqUser` / `StreamDataHost` / `StreamDataUser` | Cluster-internal service endpoints projected into each workload's `clusterDependencies.*` values. All optional — edge operators leave them empty and let per-workload `ValuesYaml` supply local equivalents. |
| `ClusterDependencies.SystemDatabaseName` / `StreamDataSchemaInstancePrefix` | Instance isolation (Epic AB#4944), projected into every workload the same way. They must mirror what the core services of the same instance run with (`serviceDefaults.systemDatabaseName` / `clusterDependencies.streamDataSchemaInstancePrefix`): a workload resolves its own tenant through the system database, so a second instance's adapters fail every CK-model load with `Tenant '<id>' does not exist` without the first, and read/write the *first* instance's CrateDB schemas without the second. Both empty on a single-instance cluster — the workload then keeps its compiled-in `OctoSystem` and the unprefixed schema names, so existing installations render byte-identically. |
| `Ingress.ClassName` / `ClusterIssuer` / `Tls` / `Annotations` | Cluster-wide ingress defaults projected into each workload's `ingress.*` values. `ClusterIssuer` is rendered into the `cert-manager.io/cluster-issuer` annotation. `Annotations` is a list of name/value pairs (env-bindable as `OPERATOR__INGRESS__ANNOTATIONS__<n>__NAME/__VALUE` because annotation keys contain dots/slashes) merged into `ingress.annotations`; an entry with the cluster-issuer key wins over `ClusterIssuer`. Per-workload public-ingress opt-in (`ingress.enabled=true` + top-level `publicUri`) comes from the workload's typed `IngressEnabled` / `Hostname` attributes via `WorkloadDeployedDto`; the cluster-wide defaults here are not overridable per workload. |
| `ClusterSecrets.MongodbUserPassword` / `MongodbAdminPassword` / `StreamDataPassword` | Data-store credentials the operator injects as secret-flagged value overrides when the workload's `ReceivesClusterSecrets` opt-in is true. The RabbitMQ password is NOT here — it stays on `BrokerPassword` and is injected unconditionally because every adapter needs the broker. Each field is optional — unset values are skipped. Operator chart wires these from a per-release Kubernetes Secret. |
| `RootCaCertificate` | PEM-encoded root CA (chain) the operator's own pod was given via the chart's `secrets.rootCa` value, forwarded here as a `secretKeyRef`-backed env var (`OPERATOR__ROOTCACERTIFICATE`). When set, injected into every workload's Helm values as `secrets.rootCa` — unconditionally, like `BrokerPassword`, and **not** secret-flagged (plain string; the workload chart's own `secrets.rootCa` template requires a literal to `b64enc`). AB#4417. |

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
- `Webhooks/CommunicationPoolValidatorTests` — pool-rtId 24-char-hex
  rule (empty, too-short, uppercase, non-hex char), poolName is
  optional, same rules enforced on update.
- `Webhooks/CommunicationPoolMutatorTests` — no-op invariant.
- `Finalizer/CommunicationPoolFinalizerTests` — success result + entity passthrough.
- `Controller/CommunicationPoolControllerTests` — `ReconcileAsync` happy/failure paths and `DeletedAsync` no-status-update contract. The delete callback must not call `IKubernetesClient.UpdateStatusAsync` because the CR is already gone when KubeOps invokes it; a status-update there 404s and makes KubeOps retry the delete reconcile indefinitely.
- `Services/OperatorHubServiceTests` — `TenantCreatedAsync` / `TenantDeletedAsync` delegate to `ICommunicationPoolManager` and swallow exceptions.

Reconcilers + Kubernetes resource managers (mocked at the abstraction boundary, not against the k8s SDK):

- `Reconcilers/WorkloadReconcilerTests/` — see the [Helm Workload Reconciliation](#helm-workload-reconciliation) section above for the test layout (deploy + undeploy + override-yaml builder).
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
