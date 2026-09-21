---
description: Why this host registers no authentication scheme and how the diagnostics endpoint is gated by remote address instead.
applies_to: src/CommunicationOperator/Controller/**, src/CommunicationOperator/Program.cs
---

# HTTP Surface — Design Notes

Why this host has no authentication and how its one administrative endpoint is protected instead.
The rules an agent has to follow are in `CLAUDE.md` → "Rules"; this file is the reasoning behind
them. Keep both in sync when the behaviour changes.

## This host has no authentication at all (AB#5059)

`Program.cs` registers `AddKubernetesOperator()`, `AddHealthChecks()` and `AddControllers()` — and
nothing else. There is **no `AddAuthentication`, no `AddAuthorization`, no `UseAuthentication`, no
`UseAuthorization`, no token authority and no JWT configuration anywhere in this repository.**

### `[Authorize]` does not gate anything here — it breaks the endpoint

Without a scheme, the attribute makes every request to it fail with
`InvalidOperationException: No authenticationScheme was specified, and there was no
DefaultChallengeScheme found`. Anyone tempted to "just add `[Authorize]`" to a controller in this
repo has to bring an authority, an OIDC client and a bearer scheme with it first.

### `Controller/DiagnosticsController` is therefore restricted by network origin

`POST system/v1/diagnostics/reconfigureLogLevel` reconfigures NLog **process-wide** and used to be
reachable by anything that could open a TCP connection to the pod — any workload in the cluster could
turn every logger to Trace (a disk / log-pipeline denial of service on a process handling broker
credentials, cluster secrets and helm values) or silence them all. It now answers **403** to any
request whose remote address is not loopback.

What remains reachable is `kubectl exec … curl` and `kubectl port-forward` — the kubelet proxies
stream *into* the pod's network namespace, so the request arrives from `127.0.0.1`. Both are gated by
Kubernetes RBAC, which is the authorization this process cannot perform but the cluster already does.

**Why it was kept rather than deleted.** No caller exists anywhere in the checkout: `octo-cli`'s
`ReconfigureLogLevel` covers identity / asset-repo / bot / communication-controller / reporting,
there is no operator service client in `octo-sdk`, and the MCP `reconfigure_log_level` tool goes to
bot services. It stays because raising the operator's log level *without restarting the pod* is
genuinely useful while a helm rollout misbehaves, and a restart loses the state one wants to see.

**Two details that are easy to get wrong.**

- A missing remote address is **not** loopback (fail closed).
- IPv4-mapped IPv6 (`::ffff:127.0.0.1`, what a dual-stack Kestrel reports) is unmapped first.
  `IPAddress.IsLoopback` does not recognise it, and getting that wrong would lock out the one access
  path being preserved.

**Route versioning.** The route keeps its literal `system/v1/[controller]`. There are no
API-versioning services in this host, so the `v1` in the template *is* the version pin. Adding
`[ApiVersion]` would mean wiring `Asp.Versioning` into an operator that publishes no API surface.

**Tests:** `tests/CommunicationOperator.Tests/Controller/DiagnosticsControllerTests.cs`.

## Outbound: the operator can now send a credential (AB#5062)

See [Operator Hub Authentication](operator-hub.md#operator-hub-authentication-ab5062). It used to send
none at all: `OperatorHubClientFactory` handed `OperatorHubClient` a freshly constructed,
never-populated `ServiceClientAccessToken`, and `SignalRClient.CreateHubConnection` in **octo-sdk**
set the literal `Authorization: Bearer your-access-token` under a `// TODO: Handle authentication`.
