---
description: Helm workload reconciliation: values layering, secret tiers, chart-version pinning, stale-lock recovery, the deploy watcher, cancellation and the scale verb.
applies_to: src/CommunicationOperator/Reconcilers/**, src/CommunicationOperator/Helm/**
---

# Workload Reconciliation — Design Notes

How Adapter and Application workloads are deployed through helm, and why each safeguard exists.

## Helm Workload Reconciliation

Workloads (Adapters + Applications) deployed to a Cloud pool are driven by
the `WorkloadReconciler` over the `helm` CLI.

**Layers:**
- `Helm/IHelmProcessInvoker` + `HelmProcessInvoker` — low-level
  `System.Diagnostics.Process` wrapper around the `helm` binary on PATH.
  Captures stdout / stderr, masks `--username` / `--password` values from
  the debug log line.
- `Helm/IHelmRunner` + `HelmRunner` — high-level operations:
  `EnsureRepoAsync` (idempotent `helm repo add --force-update` + `helm repo update`),
  `UpgradeInstallAsync` (with `-f`, `--set`, `--rollback-on-failure`),
  `UpgradeInstallDryRunAsync` (same args minus `--rollback-on-failure`, plus
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
     the controller↔adapter command bus and every adapter needs it; behind
     the data-store gate, pure edge adapters (Modbus / Loxone) fail the
     chart's mandatory `secrets.rabbitmq` check.

  2. **`secrets.rootCa`** (from `RootCaCertificate`, AB#4417) — injected
     **unconditionally** whenever `RootCaCertificate` is set, same gate
     as `BrokerPassword`. On clusters whose ingress/controller endpoint
     uses a private CA (e.g. the local kind getting-started quickstart),
     the operator pod itself trusts the CA via the chart's
     `secrets.rootCa` value, but a workload with
     `ReceivesClusterSecrets=false` (e.g. the simulation adapter) still
     opens a TLS connection to the Communication Controller and needs
     the same trust anchor or the handshake fails and the workload never
     registers. Unlike every other entry here it is **not** secret-flagged
     (`IsSecret = false`): the workload chart `b64enc`s
     `.Values.secrets.rootCa` directly and requires a plain string, so a
     `valueFrom.secretKeyRef` map there would break rendering. How `RootCaCertificate` reaches the operator
     process: `README.md` → `OPERATOR__ROOTCACERTIFICATE`.

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
  same path still wins. Each adapter chart's `secrets.*` block must
  accept both plaintext strings (legacy) and `valueFrom` maps for this
  contract to work; see `octo-mesh-adapter` / `octo-eda-adapter` chart
  `templates/_helpers.tpl` (`octo-mesh.secretEnv`). `secrets.rootCa` is
  the one exception: it is never secret-flagged, so it always renders as
  a plain literal in `values-overrides.yaml`.

## Reconciliation Keeps the Installed Chart Version (AB#4955)

An empty `ChartVersion` means "newest in the repository", resolved by helm at
`helm upgrade` time. On a deploy a human triggered that is the request. But the
controller also re-dispatches stranded `Pending` workloads on every pool
re-registration (AB#4894) — which happens on operator restarts, blueprint
re-applies, CK-model updates and `EnableCommunication`. Resolving anew there
silently upgrades workloads nobody deployed (seen on prod-1, where the new chart carried AB#4951).

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

## Stale Helm-Lock Recovery (AB#4894)

A helm process killed mid-upgrade — e.g. the operator pod replaced by a rollout while a deploy
was in flight — leaves the release's newest revision in a `pending-*` status. That lock blocks
every later install/upgrade/rollback with "another operation is in progress" and never clears
itself short of a manual Undeploy→Deploy cycle. Before the pre-flight, `WorkloadReconciler.DeployAsync` calls
`TryClearStaleHelmLockAsync`:

1. `IHelmRunner.GetLatestReleaseRevisionAsync` (`helm history {release} -o json --max 1`;
   `null` when the release does not exist).
2. Only when the newest revision `IsPending`: read the creation timestamp of the release
   secret `sh.helm.release.v1.{release}.v{rev}` via
   `IDeploymentSiteKubernetesGateway.GetSecretCreationTimestampAsync`.
3. Only when the secret is older than `WorkloadReconciler.StaleHelmLockThreshold` (default
   10 min — comfortably above helm's 5-min atomic timeout, so a live run on the outgoing pod
   of a rolling operator upgrade is never robbed of its lock): delete the secret and log a
   warning. Everything is best effort — any failure logs and lets the deploy proceed (and fail
   on the lock exactly as before).

## Pre-flight + Diagnostics

`helm upgrade --install --rollback-on-failure` collapses every failure into one
opaque stderr line — typically `Error: release X failed, and has been
uninstalled due to atomic being set: context deadline exceeded`. The
actual root cause (`ImagePullBackOff`, admission-webhook denial,
`CrashLoopBackOff`, missing secret, …) is observable on the cluster
while helm waits, but `--rollback-on-failure` rolls everything back before the
caller sees the failure. Two layers wrap the real install to surface
the actual reason:

1. **Pre-flight via `--dry-run=server`** (`UpgradeInstallDryRunAsync`,
   called before the real install in `WorkloadReconciler.DeployAsync`).
   Helm renders the chart with cluster access (`lookup` works),
   validates the rendered manifests against the cluster's OpenAPI
   schema and checks that no resource already belongs to another
   release, then returns without applying anything. It does **not**
   submit the objects with `dryRun=All`, so admission webhooks
   (Gatekeeper/Kyverno) and write RBAC are not exercised and can still
   fail the real install. What it does catch — template errors, missing
   required values, schema violations, ownership conflicts — it catches
   in seconds instead of letting the real install burn the full
   rollback-on-failure timeout. Throws `HelmException` with operation tag
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

## Live Deploy Watcher

The pre-flight / post-failure pair above still leaves a 5-minute gap
between "something is wrong" and "user sees it": helm waits its full
`--timeout` (default 5 min) before the post-failure collector runs.
`Reconcilers/WorkloadDeployWatcher` closes that gap.

Started by `WorkloadReconciler.DeployAsync` right before the real
`helm upgrade --install --rollback-on-failure`, the watcher loop:

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

## Cancellable Deploy

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

## Docker Image

`src/CommunicationOperator/Dockerfile` downloads the official `helm` tarball from `get.helm.sh`;
the baltocdn.com apt repo is blocked on the `meshmakers-ci-agents` pool, so do not switch back.
`HELM_VERSION` pins it (default `v4.2.4`; Helm 4, hence `--rollback-on-failure` rather than
`--atomic`), and Buildx forwards `TARGETARCH` for multi-arch builds. `HELM_CONFIG_HOME` /
`HELM_CACHE_HOME` / `HELM_DATA_HOME` sit under `/operator/` so the non-root `operator-user` can
write the repo cache.

## Resource Naming (`Common/K8sNaming`)

Kubernetes names are built from CK values such as `tenantId` and `workloadName`, which may contain
whitespace or uppercase letters the apiserver rejects with a 422. The workload reconciler and
`DeploymentSiteManager` both sanitise through `K8sNaming`: `DnsName` returns a strict RFC 1123
segment (lowercase `[a-z0-9-]`, dashes collapsed, 53 chars by default for parity with Helm's
release-name limit); `LabelValue` keeps the laxer label alphabet (`_` and `.` allowed, `"unknown"`
for empty input, 63 chars). `WorkloadReconciler.ReleaseName` / `SanitizeLabelValue` delegate to it
so both call sites stay in lockstep, and every generated resource keeps the original workload name
in the `octo-mesh.meshmakers.io/workload-name` annotation.

The test assembly has `InternalsVisibleTo`, so `WorkloadReconciler.ReleaseName` / `SecretName` /
`RepoAlias` can be asserted directly.

## Workload Scale Verb (AB#4917 — On-Demand Lifecycle AB#4914)

The controller's on-demand lifecycle (scale-to-zero for idle adapters) drives replica
changes through a dedicated hub callback instead of helm:

- `IOperatorHubCallbacks.ScaleWorkloadAsync(ScaleWorkloadDto)` →
  `OperatorHubService.ScaleWorkloadAsync` → `WorkloadReconciler.ScaleAsync` →
  `IDeploymentSiteKubernetesGateway.ScaleDeploymentsByInstanceAsync`. The gateway lists
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

## Adapter Pools — Platform Namespace, Owner References, Secret Tier (AB#4924 increment 5)

An **adapter pool** (`WorkloadTypeDto.AdapterPool`) is one workload with a replica range whose
members are leased to tenants in the owning tenant's subtree, one work item per lease. It goes
through the same `DeployAsync` / `UndeployAsync` / `ScaleAsync` path as every other workload — the
1:1 workload ↔ helm release model is untouched, `MinReplicas`/`MaxReplicas` are a replica count on
one release, and the AB#4917 scale verb is the scaling mechanism. Three things are different.

**1. Namespace.** `WorkloadReconciler.ResolveNamespace` sends a pool to `PlatformNamespace` and
everything else to `DeploymentSiteNamespace`. A pool member runs work for tenants other than the one that owns
it, so it deliberately does not sit among that tenant's own workloads — consumption is attributed to
the tenant whose work ran, not to the lender. Deploy, undeploy, scale, the per-release secret, the
stale-lock check and the diagnostics collector all use the resolved namespace.

🔴 **`PlatformNamespace` defaults to empty, which resolves to `DeploymentSiteNamespace`, and that is a
decision rather than a gap.** Kubernetes forbids cross-namespace owner references: a namespaced
dependent whose owner lives elsewhere is treated as having a *missing* owner and is **deleted** by
the garbage collector. The owner of a pool is the lending tenant's `DeploymentSite` CR, which
lives in `DeploymentSiteNamespace`. So the two namespaces have to be the same one for owner-reference garbage
collection to exist at all — and `DeploymentSiteNamespace` is already a platform namespace rather than a
tenant namespace, so the default satisfies both halves of the requirement. Configuring a distinct
platform namespace is supported and moves the release there, but the operator then refuses to write
the owner reference and logs why, once per deploy.

**2. Owner references.** For a pool, `DeployAsync` resolves an owner reference to the CR
`{tenantId}-{deploymentSiteRtId}` (`DeploymentSiteManager.GetCrName`, shared so both call sites cannot
drift) and puts it on the operator-owned `{release}-octo-secrets` Secret and — **after** the real
install, because helm creates them — on the release's Deployments. Deleting the tenant deletes the
CR and Kubernetes takes the pool with it, which is the safety net behind the controller's undeploy
cascade for the cases where that cascade cannot run. Every step is best effort: a missing CR, a
failed lookup or a failed patch logs and lets the deploy finish. `Controller = false` (helm manages
these objects; claiming controller ownership would make the operator their single controlling owner)
and `BlockOwnerDeletion = false` (that needs `update` on the owner's finalizers subresource and turns
a tenant delete into a wait on every dependent).

**3. Secret tier.** `AppendClusterSecrets` forces `receivesClusterSecrets` to false for a pool,
whatever the DTO says. Tiers 1 and 2 still apply — `secrets.rabbitmq` (the controller command bus)
and `secrets.rootCa` (the TLS trust anchor) — because neither carries tenant authority. Tier 3, the
cluster's *shared* Mongo / CrateDB credentials, never does: one user, every tenant's data behind it,
handed to a process whose whole purpose is to be granted one tenant at a time by a lease. The
controller refuses the same thing independently when it builds the DTO; two gates, one on each side
of the wire, because either alone is one edit away from silence.

**RBAC.** The operator's Role/ClusterRole lives in `octo-helm-core`
(`src/octo-mesh-communication-operator/templates/operator-role.yaml`), not in this repo. With
`rbac.scope: cluster` (the default binding shape) nothing has to change — the ClusterRole already
covers secrets and deployments in every namespace. With `rbac.scope: namespace` the Role is bound in
the release namespace only, so a **distinct** platform namespace needs its own Role + RoleBinding
there. A missing grant surfaces on the `--dry-run=server` pre-flight rather than mid-rollout, which
is what that pre-flight is for.

