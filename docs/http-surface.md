---
description: Why this host registers no authentication scheme and how the diagnostics endpoint is gated by remote address instead.
applies_to: src/CommunicationOperator/Controller/**, src/CommunicationOperator/Program.cs, src/CommunicationOperator/Services/*Diagnostics*.cs
---

# HTTP Surface — Design Notes

Why this host has no authentication and how its one administrative endpoint is protected instead.
The rules are in `AGENTS.md` → "Rules"; this file is the reasoning behind them.

## This host has no authentication at all (AB#5059)

`Program.cs` registers `AddKubernetesOperator()`, `AddHealthChecks()` and `AddControllers()` — and
nothing else. There is **no `AddAuthentication`, no `AddAuthorization`, no `UseAuthentication`, no
`UseAuthorization`, no token authority and no JWT configuration anywhere in this repository.**

So `[Authorize]` does not gate an endpoint here — it breaks it (`InvalidOperationException: No
authenticationScheme was specified, and there was no DefaultChallengeScheme found`).

### `Controller/DiagnosticsController` is therefore restricted by network origin

`POST system/v1/diagnostics/reconfigureLogLevel` reconfigures NLog **process-wide**, so it answers
**403** to any request whose remote address is not loopback. Open, any workload in the cluster could
turn every logger to Trace (a disk / log-pipeline denial of service on a process handling broker
credentials, cluster secrets and helm values) or silence them all.

What remains reachable is `kubectl exec … curl` and `kubectl port-forward` — the kubelet proxies
stream *into* the pod's network namespace, so the request arrives from `127.0.0.1`. Both are gated by
Kubernetes RBAC, which is the authorization this process cannot perform but the cluster already does.

**Do not delete it as dead code.** Nothing in the checkout calls it (`octo-cli`'s
`ReconfigureLogLevel` and the MCP `reconfigure_log_level` tool target other services), but raising
the log level *without restarting the pod* is what you need while a helm rollout misbehaves — a
restart loses the state you want to see.

**Two details that are easy to get wrong.**

- A missing remote address is **not** loopback (fail closed).
- IPv4-mapped IPv6 (`::ffff:127.0.0.1`, what a dual-stack Kestrel reports) is unmapped first.
  `IPAddress.IsLoopback` does not recognise it, and getting that wrong would lock out the one access
  path being preserved.

**Tests:** `tests/CommunicationOperator.Tests/Controller/DiagnosticsControllerTests.cs`.

## Outbound authentication (AB#5062)

The operator's own credential on `/operatorHub` is covered in
[Operator Hub Authentication](operator-hub.md#operator-hub-authentication-ab5062).
