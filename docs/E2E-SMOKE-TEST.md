# E2E Smoke Test — Central Operator Mode

This runbook validates the central-operator code path end-to-end against a
real local stack: tenant lifecycle events flow from the Communication
Controller via SignalR into the Operator, and the Operator creates / deletes
`CommunicationPool` custom resources and broker secrets in a kind cluster.

The test is **manual** — it is not part of CI. Run it after non-trivial
changes in `OperatorHubService`, `CommunicationPoolManager`, or any of the
SDK / Controller plumbing they depend on.

## What we verify

```
[POST /system/v1/communication/enable?tenantId=e2etest]   ← octo-cli
            ↓
[Communication Controller → /operatorHub SignalR push]
            ↓
[OperatorHubService.TenantCreatedAsync]
            ↓
[CommunicationPoolManager.CreateCommunicationPoolAsync]
            ↓
[real k8s API call via ICommunicationPoolKubernetesGateway]
            ↓
[CR + Secret in 'octo' namespace of the kind cluster]   ← kubectl assertion
```

The same path in reverse for `DisableCommunication`.

## Prerequisites

| Tool / Component | Why |
|---|---|
| `kind` on PATH | Local Kubernetes cluster |
| `helm` on PATH | Installs the `octo-mesh-crds` chart |
| `kubectl` on PATH | Cluster assertions |
| `docker` running | Mongo + RabbitMQ containers |
| `octo-cli` on PATH | Login + `EnableCommunication` / `DisableCommunication` |
| `octo-tools` PowerShell modules loaded | `Install-OctoInfrastructure`, `Install-OctoKubernetes`, `Invoke-BuildAll`, `Start-Octo` |
| `.NET 10 SDK` | Builds and runs all services |
| `octo-communication-operator`, `octo-helm-core`, plus all `octo-*` backend repos checked out under `$rootPath` | Source for build + chart |

## One-time setup

Run these once per workstation. Each step is idempotent.

```powershell
# Mongo replica set + RabbitMQ via docker compose
Install-OctoInfrastructure

# kind cluster (default name "kind"), octo-mesh-crds Helm chart,
# 'octo' pool namespace
Install-OctoKubernetes

# Build everything (operator + controller + identity + ...) in Release
Invoke-BuildAll -configuration Release
```

`Install-OctoKubernetes` prints the active kubectl context at the end. If it
is not `kind-kind`, run:

```powershell
kubectl config use-context kind-kind
```

before continuing.

## Run the stack

In one terminal:

```powershell
Start-Octo
```

Wait until each backend service prints its "Now listening on …" line and
status is `Running` for every job.

In a second terminal:

```powershell
cd $rootPath/octo-communication-operator
./start-operator.ps1
```

The operator binds to `http://localhost:5022` and `https://localhost:5023`,
loads `appsettings.Development.json`, and connects to the Communication
Controller's `/operatorHub` SignalR endpoint at `https://localhost:5015`.

**Wait for** the log line:

```
info: Meshmakers.Octo.Communication.Operator.Services.OperatorHubService[0]
      Operator hub connected, waiting for tenant events
```

If this line never appears, the Controller is not reachable — see
[Troubleshooting](#troubleshooting).

## Test procedure

### 1. Authenticate

```powershell
Invoke-OctoCliLoginLocal -tenantId admin
octo-cli -c LogIn -i
```

Follow the device-code flow in the browser. After the CLI confirms the
token, verify with:

```powershell
octo-cli -c AuthStatus
```

### 2. Trigger CommunicationPool creation

```powershell
octo-cli -c EnableCommunication -tid e2etest
```

The operator's log should print, within a second or two:

```
info: Meshmakers.Octo.Communication.Operator.Services.OperatorHubService[0]
      Tenant created event received: e2etest
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      Creating broker secret 'e2etest-default-octo-mesh-connection' in namespace 'octo'
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      Creating CommunicationPool CR 'e2etest-default' in namespace 'octo' for tenant 'e2etest'
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      CommunicationPool CR 'e2etest-default' created successfully
```

### 3. Verify the cluster state

```bash
kubectl get communicationpool -n octo
```

Expected:

```
NAME              AGE
e2etest-default   3s
```

```bash
kubectl get secret -n octo e2etest-default-octo-mesh-connection \
  -o jsonpath='{.data.brokerusername}' | base64 -d
```

Expected: `guest` (matches `appsettings.Development.json`).

```bash
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

### 4. Trigger CommunicationPool deletion

```powershell
octo-cli -c DisableCommunication -tid e2etest
```

The operator's log should print:

```
info: Meshmakers.Octo.Communication.Operator.Services.OperatorHubService[0]
      Tenant deleted event received: e2etest
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      Deleting CommunicationPool CR 'e2etest-default' in namespace 'octo' for tenant 'e2etest'
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      Deleting broker secret 'e2etest-default-octo-mesh-connection' in namespace 'octo'
info: Meshmakers.Octo.Communication.Operator.Services.CommunicationPoolManager[0]
      CommunicationPool CR 'e2etest-default' deleted successfully
```

### 5. Verify cleanup

```bash
kubectl get communicationpool -n octo
# expected: No resources found in octo namespace.

kubectl get secret -n octo e2etest-default-octo-mesh-connection
# expected: NotFound error
```

## Stopping the stack

Press a key in the `Start-Octo` terminal to stop the backend jobs. Press
`Ctrl+C` in the `start-operator.ps1` terminal to stop the operator.

The kind cluster, Helm release, and docker containers stay up — re-runs are
fast.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Operator log never prints "Operator hub connected" | Controller not running, port 5015 blocked, or `appsettings.Development.json` URI mismatch. Check `logFiles/CommunicationControllerServices.log`. |
| `Tenant created event received` fires but no CR appears | RBAC against kind. Run `kubectl auth can-i create communicationpools.octo-mesh.meshmakers.io -n octo`. With kind's default kubeconfig you should be cluster-admin. |
| `kubectl get communicationpool` returns `error: the server doesn't have a resource type "communicationpool"` | CRDs chart not installed. Re-run `Install-OctoKubernetes`. |
| `octo-cli -c EnableCommunication` returns 401 / 403 | Re-run `octo-cli -c LogIn -i`. The device-code token is short-lived. |
| `octo-cli` cannot find the controller | Re-run `Invoke-OctoCliLoginLocal` to point at `https://localhost:5015`. |
| `CR' already exists` log entry | Previous run did not clean up. Either re-issue `DisableCommunication` or `kubectl delete communicationpool e2etest-default -n octo` and rerun. |

Operator log: stdout of the `start-operator.ps1` terminal. Other services
log to `$rootPath/logFiles/<ServiceName>.log` (managed by `Start-Octo`).
