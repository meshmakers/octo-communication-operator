# E2E Smoke Test — Central Operator Mode

This runbook validates the central-operator code path end-to-end against a
real local stack: deploying a Cloud pool from the Refinery Studio triggers
the Communication Controller to push a SignalR event to the Operator, and
the Operator creates a `CommunicationPool` custom resource and broker secret
in a kind cluster. Undeploying tears them down.

The test is **manual** — it is not part of CI. Run it after non-trivial
changes in `OperatorHubService`, `CommunicationPoolManager`, `PoolService`
(controller), or any of the SDK / Helm plumbing they depend on.

## What we verify

```
[Refinery Studio → POST {tenantId}/v1/pool/deploy?poolRtId=<id>]
            ↓
[Controller PoolService.DeployPoolAsync]
            ↓  (only when RtPool.Environment == Cloud)
[Controller /operatorHub SignalR push → PoolDeployedAsync]
            ↓
[OperatorHubService.PoolDeployedAsync]
            ↓
[CommunicationPoolManager.CreatePoolAsync(tenantId, poolName)]
            ↓
[real k8s API call via ICommunicationPoolKubernetesGateway]
            ↓
[CR + Secret in 'octo' namespace of the kind cluster]  ← kubectl assertion
```

The same path in reverse for `Undeploy Pool`. **Edge pools** transition
state without an operator notification — they are installed and run by an
external operator outside the central cluster and are explicitly out of
scope for this runbook.

## Prerequisites

| Tool / Component | Why |
|---|---|
| `kind` on PATH | Local Kubernetes cluster |
| `helm` on PATH | Installs the `octo-mesh-crds` chart |
| `kubectl` on PATH | Cluster assertions |
| `docker` running | Mongo + RabbitMQ containers |
| `octo-cli` on PATH | Login + tenant create |
| `octo-tools` PowerShell modules loaded | `Install-OctoInfrastructure`, `Install-OctoKubernetes`, `Invoke-BuildAll`, `Start-Octo` |
| `.NET 10 SDK` | Builds and runs all services |
| Node 22+ and a recent npm | Refinery Studio dev server |
| `octo-communication-operator`, `octo-communication-controller-services`, `octo-helm-core`, `octo-frontend-refinery-studio`, plus all `octo-*` backend repos checked out under `$rootPath` | Source for build + chart + UI |

## One-time setup

Run these once per workstation. Each step is idempotent.

```powershell
# Mongo replica set + RabbitMQ via docker compose
Install-OctoInfrastructure

# kind cluster (default name "kind"), octo-mesh-crds Helm chart,
# 'octo' pool namespace
Install-OctoKubernetes

# Build everything (operator + controller + identity + ...). DebugL matches
# the standard local-dev convention (Octo packages come from ../nuget/, can
# step-debug across repos). Use Release only when you want to mirror CI.
Invoke-BuildAll -configuration DebugL
```

`Install-OctoKubernetes` prints the active kubectl context at the end. If
it is not `kind-kind`, run:

```powershell
kubectl config use-context kind-kind
```

before continuing.

Also: kill any stale **MutatingWebhookConfiguration** / **ValidatingWebhookConfiguration**
that point at a previous in-cluster operator deployment — otherwise the
kube-apiserver will try to call a dead webhook URL and `CR create` returns
500. Both deletes are idempotent:

```powershell
kubectl get mutatingwebhookconfiguration  | Select-String dev-mutators
kubectl get validatingwebhookconfiguration | Select-String dev-validators
kubectl delete mutatingwebhookconfiguration  dev-mutators   --ignore-not-found
kubectl delete validatingwebhookconfiguration dev-validators --ignore-not-found
```

## Run the stack

In one terminal — backend services:

```powershell
Start-Octo -configuration DebugL
```

(`Start-Octo` defaults to Release; pass `-configuration DebugL` if you built
that way. The configurations must match — both scripts read binaries from
`bin/<configuration>/net10.0/`.)

Wait until each backend service prints its "Now listening on …" line and
status is `Running` for every job.

In a second terminal — the operator (NOT auto-started by `Start-Octo`):

```powershell
cd $rootPath/octo-communication-operator
./start-operator.ps1 -configuration DebugL
```

The operator binds to `http://localhost:5022` and `https://localhost:5023`,
loads `appsettings.Development.json`, and connects to the Communication
Controller's `/operatorHub` SignalR endpoint at `https://localhost:5015`.

**Wait for** the log line:

```
info: Meshmakers.Octo.Communication.Operator.Services.OperatorHubService[0]
      Operator hub connected, waiting for pool events
```

If this line never appears, the Controller is not reachable — see
[Troubleshooting](#troubleshooting).

In a third terminal — the Refinery Studio dev server:

```powershell
cd $rootPath/octo-frontend-refinery-studio/src/octo-mesh-refinery-studio
npm start
```

Wait until Angular reports "Compiled successfully" and serves on
`https://localhost:4200`.

## Test procedure

### 1. Authenticate as OctoSystem admin

```powershell
Invoke-OctoCliLoginLocal -tenantId OctoSystem
# Browser opens, complete the device-code flow.
octo-cli -c AuthStatus
# Expected: a valid access token with a future expiry.
```

### 1a. Make sure the test tenant exists

```powershell
octo-cli -c Create -tid e2etest -db e2etest
```

The OctoSystem user that just authenticated is auto-provisioned as admin in
the new tenant. If the tenant already exists, the command reports "already
exists" and is safe to skip.

### 2. Create a Cloud pool

In the Refinery Studio, sign in to the `e2etest` tenant and navigate to:

```
https://localhost:4200/e2etest/communication/pools
```

Click **New Pool** in the toolbar, then fill out the form:

| Field | Value |
|---|---|
| Name | `default` |
| Description | (anything, e.g. "E2E smoke test pool") |
| **Environment** | **Cloud** |

Save the pool. It appears in the list with `Environment = CLOUD`,
`DeploymentState = UNDEPLOYED`.

### 3. Deploy the pool

Right-click the pool row (or use the action menu) → **Deploy Pool** →
confirm the dialog.

**Within 1–2 seconds**, the operator log should print:

```
Pool deployed event received: tenant 'e2etest', pool 'default'
Creating broker secret 'e2etest-default-octo-mesh-connection' in namespace 'octo'
Creating CommunicationPool CR 'e2etest-default' in namespace 'octo' for tenant 'e2etest', pool 'default'
CommunicationPool CR 'e2etest-default' created successfully
```

The pool row in the list refreshes to `DeploymentState = DEPLOYED`.

### 4. Verify the cluster state

```powershell
kubectl get communicationpool -n octo
```

Expected:

```
NAME              AGE
e2etest-default   3s
```

```powershell
kubectl get secret -n octo e2etest-default-octo-mesh-connection `
  -o jsonpath='{.data.brokerusername}' | base64 -d
```

Expected: `guest` (matches `appsettings.Development.json`).

```powershell
kubectl get communicationpool e2etest-default -n octo -o yaml
```

Expected `spec`:

```yaml
spec:
  tenantId: e2etest
  poolName: default
  communicationControllerUri: https://localhost:5015
  brokerHost: localhost
  brokerPort: 5672
  brokerVirtualHost: /
```

And the labels on both the CR and the secret should include:

```yaml
labels:
  octo-mesh.meshmakers.io/tenant: e2etest
  octo-mesh.meshmakers.io/pool: default
  octo-mesh.meshmakers.io/managed-by: communication-operator
```

### 5. Undeploy the pool

Back in the Refinery Studio pool list, right-click the pool → **Undeploy
Pool** → confirm.

The operator log should print:

```
Pool undeployed event received: tenant 'e2etest', pool 'default'
Deleting CommunicationPool CR 'e2etest-default' in namespace 'octo' for tenant 'e2etest', pool 'default'
Deleting broker secret 'e2etest-default-octo-mesh-connection' in namespace 'octo'
CommunicationPool CR 'e2etest-default' deleted successfully
```

The pool row refreshes to `DeploymentState = UNDEPLOYED`.

### 6. Verify cleanup

```powershell
kubectl get communicationpool -n octo
# expected: No resources found in octo namespace.

kubectl get secret -n octo e2etest-default-octo-mesh-connection
# expected: NotFound error
```

### 7. Bonus — tenant-delete cascade

If you delete the `e2etest` tenant from the OctoSystem context while it
still has a deployed Cloud pool, the controller's `TenantManagementConsumer`
calls `PoolService.UndeployAllCloudPoolsAsync(tenantId)` in the
`PreDeleteTenant` consumer. That fires a `PoolUndeployedAsync` event for
every Cloud pool of the tenant, so the operator cleans up its CRs/secrets
before the tenant data is gone.

To verify: redeploy the pool (step 3), then from the OctoSystem CLI context:

```powershell
Invoke-OctoCliLoginLocal -tenantId OctoSystem
octo-cli -c Delete -tid e2etest -y
```

Expect the same "Deleting CommunicationPool CR …" log lines as in step 5,
followed by an empty `kubectl get communicationpool -n octo`.

## Stopping the stack

Press a key in the `Start-Octo` terminal to stop the backend jobs. Press
`Ctrl+C` in the `start-operator.ps1` terminal to stop the operator. Stop
the Studio dev server with `Ctrl+C` as well.

The kind cluster, Helm release, and docker containers stay up — re-runs
are fast.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Operator log never prints "Operator hub connected, waiting for pool events" | Controller not running, port 5015 blocked, or `appsettings.Development.json` URI mismatch. Check `logFiles/CommunicationControllerServices.log`. |
| `Pool deployed event received` fires but no CR appears | RBAC against kind. Run `kubectl auth can-i create communicationpools.octo-mesh.meshmakers.io -n octo`. With kind's default kubeconfig you should be cluster-admin. |
| Operator log: `Internal error occurred: failed calling webhook "mutate.communicationpool…": connect: connection refused` | A stale `dev-mutators` / `dev-validators` webhook config from a previous in-cluster operator deploy still points at a dead URL. Delete both (see [One-time setup](#one-time-setup)). |
| `kubectl get communicationpool` returns `error: the server doesn't have a resource type "communicationpool"` | CRDs chart not installed. Re-run `Install-OctoKubernetes`. |
| **Deploy Pool** does nothing — operator log silent | The pool's `Environment` is set to `Edge`. Open the form, switch to `Cloud`, save, then redeploy. |
| Refinery Studio `Deploy Pool` action missing from the context menu | The `@meshmakers/octo-services` library was not rebuilt or `npm install` was not run in `octo-mesh-refinery-studio` after the library change. From `octo-frontend-libraries/src/frontend-libraries/`: `npm run build:octo-services`. From `octo-mesh-refinery-studio/`: `npm install`. |
| `octo-cli` cannot find the controller | Re-run `Invoke-OctoCliLoginLocal -tenantId <tenant>` to point at `https://localhost:5015`. |
| `CR already exists` log entry on redeploy | Previous run did not clean up. Click **Undeploy Pool** in the Studio (or `kubectl delete communicationpool e2etest-default -n octo`) and retry. |

Operator log: stdout of the `start-operator.ps1` terminal. Other services
log to `$rootPath/logFiles/<ServiceName>.log` (managed by `Start-Octo`).
Studio log: stdout of the `npm start` terminal plus the browser dev tools.
