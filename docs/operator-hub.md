---
description: The /operatorHub SignalR connection: lifecycle in central vs edge mode, pool-registration retry, reverse-sync on reconnect, the operator's own access token, and the two test seams.
applies_to: src/CommunicationOperator/Services/**
---

# Operator Hub Connection — Design Notes

Design notes for `src/CommunicationOperator/Services`: the SignalR `/operatorHub` connection to
the Communication Controller, pool registration and its retry / reverse-sync paths, the operator's
own access token, and the two test seams that make the service testable. Split out of the root
`CLAUDE.md` so the always-loaded instructions stay short. Keep this file in sync when the
behaviour changes (see "Mandatory before commit" in `CLAUDE.md`).

## OperatorHubService Lifecycle (Central + Edge)

`OperatorHubService` (a `BackgroundService`) opens a SignalR connection to the Controller's `/operatorHub` **whenever `OPERATOR__COMMUNICATIONCONTROLLERURI` is configured** — required in both central and edge modes. Without this connection the operator's `IOperatorHubInvoker.RegisterPoolAsync` no-ops, and pools registered through `CommunicationPoolController.ReconcileAsync` never reach the controller (the entity stays at `Unregistered` in the Studio UI). The previous early-return on `!AutoManagePools` was the cause of the regression where edge-cluster pools showed up as Unregistered indefinitely.

`OPERATOR__AUTOMANAGEPOOLS` is now a narrower flag — it only gates the **side effect of auto-creating / -deleting `CommunicationPool` CRs** in response to controller broadcasts:

- `AutoManagePools=true` (central): `PoolDeployedAsync` → `CommunicationPoolManager.CreateCommunicationPoolAsync` (creates the CR + broker secret, idempotent). `PoolUndeployedAsync` → `DeleteCommunicationPoolAsync`. `RegisterOperatorAsync()` on (re)connect also fans out `CreatePoolAsync` for every already-deployed pool.
- `AutoManagePools=false` (edge): `PoolDeployedAsync` / `PoolUndeployedAsync` log + return without touching `ICommunicationPoolManager`, **and** the `RegisterOperatorAsync()` reconnect fan-out is gated by the same flag. The latter gate is load-bearing: without it, every edge-operator pod restart would materialize a CR + broker secret for every Cloud pool the controller knows about, and the operator would then `RegisterPoolAsync` them — putting workload-deploy events on a route that also lands on the edge cluster. CRs on the edge cluster are managed manually or by an external system.

Either way, the workload-deploy path (`WorkloadDeployedAsync` → `WorkloadReconciler.DeployAsync`) and the pool register/unregister round-trip from `CommunicationPoolController.ReconcileAsync` go through the same SignalR client.

The connection is auto-reconnecting via `OperatorHubClient`. Failures from the pool manager and workload reconciler are logged but **not propagated** so that one bad event cannot break the hub connection.

### Unregister is a soft failure

`PoolService.UnRegisterPoolAsync` (called from `CommunicationPoolController.DeletedAsync`) treats any
`HubException` from the controller-side `UnregisterPoolOperatorAsync` call as a **soft failure** and
only logs it. Reason: the CR is already gone when `DeletedAsync` fires, and during the tenant-delete
cascade the tenant itself no longer exists at the controller — so the unregister roundtrip will
respond with `TenantException`. Re-throwing would put the entity back in the KubeOps retry queue
forever. The local connection is still stopped and the pool removed from `_pools` regardless.

## Pool-Registration Retry Loop (AB#4371)

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

## Reverse-Sync on Reconnect

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

## Operator Hub Authentication (AB#5062)

Configuration, the choice of tenant and the rollout order are in `README.md` →
[Operator hub authentication](../README.md#operator-hub-authentication-ab5062). This section covers
only the code path.

The operator obtains its own client-credentials access token and presents it on the `/operatorHub`
connection. Four pieces, in the order the token travels:

1. **`OperatorOptions.Authentication`** (`OperatorAuthenticationOptions`: `IssuerUri`, `ClientId`,
   `ClientSecret`, `TenantId`; env `OPERATOR__AUTHENTICATION__…`). `IsEnabled` is
   `IssuerUri && ClientId` — the secret is deliberately not part of the check, so a confidential
   client with a missing secret fails loudly at the token endpoint instead of silently degrading to
   an anonymous connection that looks healthy until the gate is armed.
2. **`ConfigureOperatorAuthenticatorOptions`** (`IConfigureOptions<AuthenticatorOptions>`) projects
   that onto the SDK's `AuthenticatorOptions`, which `AuthenticatorClient` reads. An
   `IConfigureOptions` rather than an inline delegate in `Program.cs` so the projection — above all
   `TenantId`, whose loss produces an AB#5058 `invalid_request` that points nowhere near the mapping
   — is pinned by a test.
3. **`OperatorAccessTokenService`** (`BackgroundService`) requests the token
   (`ApiScopes.OctoApiFullAccess`, `DefaultScopes.None` → exactly `octo_api`, no `offline_access`)
   and writes it into the singleton `IServiceClientAccessToken`. It also owns the refresh loop:
   replace the token `RefreshSkew` (5 min) before its own `exp`, retry every `RetryInterval` (30 s)
   after a failure, and **keep the previous token on failure** — it is no less usable than none, and
   dropping it would guarantee a refusal for what is usually a transient identity blip.
4. **`OperatorHubClientFactory`** hands that *same instance* to `OperatorHubClient`. The SDK reads it
   through `HttpConnectionOptions.AccessTokenProvider` on every connection attempt, so a refresh
   reaches the next (re)connect with no notification path. The old per-client
   `new ServiceClientAccessToken()` was unreachable by construction.

**Startup ordering is load-bearing.** The first acquisition runs inside `StartAsync`, before
`base.StartAsync`; hosted services start sequentially and this one is registered before
`OperatorHubService`, so the first hub connection already carries a token rather than racing it.

🔴 **Unconfigured is a supported state and must stay one.** Without a client id the service logs one
warning, never calls the authenticator, and the access token stays null — which makes the SDK send no
`Authorization` header and no `access_token` query parameter at all, i.e. exactly today's connection.
Every operator in the estate runs without these keys.

## `IOperatorHubClientFactory` — the seam for `OperatorHubClient`

`OperatorHubService.ExecuteAsync` originally `new`'d an `OperatorHubClient` directly, which made the SignalR connection logic untestable. The factory interface produces an `IOperatorHubClient` (already exposed by the SDK) and is mocked in tests. Production wiring lives in `OperatorHubClientFactory` (registered as singleton in `Program.cs`).

Tests use `client.When(c => c.EnableReconnect(...)).Do(_ => tcs.TrySetResult())` as a sync point — once `EnableReconnect` has been called, the connect callback has already finished and the service is parked in `Task.Delay(Infinite, stoppingToken)`. Asserting before that yields race conditions where the assertion runs before `ExecuteAsync` reaches the verified line.

## `ICommunicationPoolKubernetesGateway` — the seam for `IKubernetes`

`CommunicationPoolManager` originally talked directly to `IKubernetes` and used a stack of extension methods (`CustomObjects.GetNamespacedCustomObjectAsync`, `CoreV1.ReadNamespacedSecretAsync`, …). Mocking that surface is verbose because:
- the extensions delegate to nested sub-interfaces (`ICustomObjectsOperations`, `ICoreV1Operations`),
- the `Exists`-via-404 idiom requires throwing `HttpOperationException` with a fake `HttpResponseMessageWrapper`,
- assertions then have to target the underlying `*WithHttpMessagesAsync` method names rather than the readable extension API.

The `ICommunicationPoolKubernetesGateway` interface (in `Services/`) collapses that surface to six methods: `CommunicationPoolExistsAsync`, `CreateCommunicationPoolAsync`, `DeleteCommunicationPoolAsync`, `SecretExistsAsync`, `CreateSecretAsync`, `DeleteSecretAsync`. The implementation `CommunicationPoolKubernetesGateway` keeps every k8s-SDK quirk (404 → `false`, extension-method routing, CRD group/version/plural constants) in one place. Add new k8s calls to the interface — don't reach back into `IKubernetes` from elsewhere.

