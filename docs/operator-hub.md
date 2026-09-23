---
description: The /operatorHub SignalR connection: lifecycle in central and edge mode, pool-registration retry, reverse-sync, the operator's access token and the test seams.
applies_to: src/CommunicationOperator/Services/**
---

# Operator Hub Connection — Design Notes

The SignalR `/operatorHub` connection to the Communication Controller: registration and its retry /
reverse-sync paths, the operator's own access token, and the two test seams.

## OperatorHubService Lifecycle (Central + Edge)

`OperatorHubService` (a `BackgroundService`) opens a SignalR connection to the Controller's `/operatorHub` **whenever `OPERATOR__COMMUNICATIONCONTROLLERURI` is configured** — required in both central and edge modes. Without this connection the operator's `IOperatorHubInvoker.RegisterDeploymentSiteAsync` no-ops, and pools registered through `DeploymentSiteController.ReconcileAsync` never reach the controller (the entity stays at `Unregistered` in the Studio UI). Never gate the connection on `AutoManageDeploymentSites`: that early return is what left edge-cluster pools `Unregistered` indefinitely.

`OPERATOR__AUTOMANAGEDEPLOYMENTSITES` is a narrower flag — it only gates the **side effect of auto-creating / -deleting `DeploymentSite` CRs** in response to controller broadcasts:

- `AutoManageDeploymentSites=true` (central): `DeploymentSiteDeployedAsync` → `DeploymentSiteManager.CreateDeploymentSiteAsync` (creates the CR + broker secret, idempotent). `DeploymentSiteUndeployedAsync` → `DeleteDeploymentSiteAsync`. `RegisterOperatorAsync()` on (re)connect also fans out `CreateDeploymentSiteAsync` for every already-deployed pool.
- `AutoManageDeploymentSites=false` (edge): `DeploymentSiteDeployedAsync` / `DeploymentSiteUndeployedAsync` log + return without touching `IDeploymentSiteManager`, **and** the `RegisterOperatorAsync()` reconnect fan-out is gated by the same flag. The latter gate is load-bearing: without it, every edge-operator pod restart would materialize a CR + broker secret for every Cloud pool the controller knows about, and the operator would then `RegisterDeploymentSiteAsync` them — putting workload-deploy events on a route that also lands on the edge cluster. CRs on the edge cluster are managed manually or by an external system.

Either way, the workload-deploy path (`WorkloadDeployedAsync` → `WorkloadReconciler.DeployAsync`) and the pool register/unregister round-trip from `DeploymentSiteController.ReconcileAsync` go through the same SignalR client.

The connection is auto-reconnecting via `OperatorHubClient`. Failures from the pool manager and workload reconciler are logged but **not propagated** so that one bad event cannot break the hub connection.

### Unregister is a soft failure

`DeploymentSiteService.UnRegisterDeploymentSiteAsync` (called from `DeploymentSiteController.DeletedAsync`) treats any
`HubException` from the controller-side `UnregisterPoolOperatorAsync` call as a **soft failure** and
only logs it. Reason: the CR is already gone when `DeletedAsync` fires, and during the tenant-delete
cascade the tenant itself no longer exists at the controller — so the unregister roundtrip will
respond with `TenantException`. Re-throwing would put the entity back in the KubeOps retry queue
forever. Locally the site is removed from `_deploymentSites` and its `IsRegistered` flag cleared
regardless; the SignalR connection is shared by every site and is not touched.

## Pool-Registration Retry Loop (AB#4371)

A registration the **controller rejects while the SignalR connection stays alive** would otherwise
be lost: the reconnect callback is the only other trigger, and it fires only when the connection
drops. The pool then stays orphaned and the controller drops its workload events (seen on prod-1
when the operator reconnected before the controller's CkCache had loaded tenant models).

`OperatorHubService.RetryDeploymentSiteRegistrationLoopAsync` closes the gap:

- Started once in `ExecuteAsync`, runs for the service lifetime, cadence
  `OperatorOptions.DeploymentSiteRegistrationRetrySeconds` (default 30, fractional
  values allowed for tests, `<= 0` disables with a warning).
- Each tick (only while `client.IsAlive`): registers every owned pool with
  `IsRegistered == false`, flips the flag on success, and fires the per-pool
  reverse-sync (`ReportDeployedDeploymentSiteAsync`) so a drifted `DeploymentState` is
  restored. Failures are logged and retried on the next tick.
- The reconnect callback calls `DeploymentSiteService.ResetRegistrationState()`
  **before** replaying registrations — a pool registered on a previous
  connection that fails re-registration would otherwise keep a stale
  `IsRegistered=true` and be invisible to the retry loop.

Registration is idempotent on the controller (`RegisterPoolForConnection`
is a set-add; the state write is guarded), so a retry racing a reconnect
replay is harmless.

## Reverse-Sync on Reconnect

After the operator has re-registered every owned `DeploymentSite` CR
with the controller (the `RegisterDeploymentSiteAsync` loop in `onReconnect`), a
**Cloud operator** (`AutoManageDeploymentSites=true`) follows up with one call to
`IOperatorHub.ReportDeployedStateAsync(reports)` carrying the set of
pools it currently has CRs for. The controller restores
`DeploymentState=Deployed` on any pool whose state drifted while the
operator was offline (e.g. controller restart between deploys lost the
in-memory `OperatorConnectionManager` tracking) and rebuilds the
per-connection pool registration so undeploy fan-out keeps working.

**Two coupled paths run the reverse-sync:**

1. **Bulk on reconnect** (`OperatorHubService.onReconnect`): captures
   the snapshot of `deploymentSiteService.GetDeploymentSites()` when the SignalR connect
   callback fires and sends them all in one call. Works for the
   *controller-restart* case where the operator's KubeOps cache was
   never torn down — every CR is in `_deploymentSites` by the time the callback
   runs.
2. **Per-pool on register** (`DeploymentSiteService.RegisterDeploymentSiteAsync` →
   `IOperatorHubInvoker.ReportDeployedDeploymentSiteAsync`): every CR reconcile
   that registers a pool also fires a single-pool reverse-sync. Closes
   the *operator-restart* race where KubeOps populates `_deploymentSites`
   AFTER the bulk callback already ran: CRs discovered later than the
   snapshot would otherwise miss their restore window and stay stuck
   at whatever drifted state the controller had on them. Per-pool is
   idempotent on the controller side (restore-only-when-changed) so the
   double coverage doesn't spam audit events.

Gating:

- `AutoManageDeploymentSites=false` (edge): the operator skips the call entirely.
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
controller-side restore handles empty lists cleanly. Closing the gap needs the rtIds stored where a
restart can read them back (a helm `--labels` entry or the `{release}-octo-secrets` Secret); the
contract is in `docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md`.

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
   reaches the next (re)connect with no notification path. Never construct a
   per-client token: nothing could ever refresh it.

**Startup ordering is load-bearing.** The first acquisition runs inside `StartAsync`, before
`base.StartAsync`; hosted services start sequentially and this one is registered before
`OperatorHubService`, so the first hub connection already carries a token rather than racing it.

🔴 **Unconfigured is a supported state and must stay one.** Without a client id the service logs one
warning, never calls the authenticator, and the access token stays null — which makes the SDK send no
`Authorization` header and no `access_token` query parameter at all, i.e. exactly today's connection.
Every operator in the estate runs without these keys.

## `IOperatorHubClientFactory` — the seam for `OperatorHubClient`

Constructing `OperatorHubClient` inside `OperatorHubService.ExecuteAsync` would make the connection logic untestable, so a factory produces an `IOperatorHubClient` (already exposed by the SDK) and is mocked in tests. Production wiring lives in `OperatorHubClientFactory` (registered as singleton in `Program.cs`).

Tests use `client.When(c => c.EnableReconnect(...)).Do(_ => tcs.TrySetResult())` as a sync point — once `EnableReconnect` has been called, the connect callback has already finished and the service is parked in `Task.Delay(Infinite, stoppingToken)`. Asserting before that yields race conditions where the assertion runs before `ExecuteAsync` reaches the verified line.

## `IDeploymentSiteKubernetesGateway` — the seam for `IKubernetes`

Calling `IKubernetes` directly means a stack of extension methods (`CustomObjects.GetNamespacedCustomObjectAsync`, `CoreV1.ReadNamespacedSecretAsync`, …), and mocking that surface is verbose:
- the extensions delegate to nested sub-interfaces (`ICustomObjectsOperations`, `ICoreV1Operations`),
- the `Exists`-via-404 idiom requires throwing `HttpOperationException` with a fake `HttpResponseMessageWrapper`,
- assertions then have to target the underlying `*WithHttpMessagesAsync` method names rather than the readable extension API.

The `IDeploymentSiteKubernetesGateway` interface (in `Services/`) collapses that surface to intent-named methods (`DeploymentSiteExistsAsync`, `CreateSecretAsync`, `ScaleDeploymentsByInstanceAsync`, …), and `DeploymentSiteKubernetesGateway` keeps every k8s-SDK quirk (404 → `false`, extension-method routing, CRD group/version/plural constants) in one place.

