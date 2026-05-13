# E2E Smoke Test — Central Operator Mode

This runbook validates the central-operator code path end-to-end against a
real local stack. Deploying a Cloud pool from the Refinery Studio triggers
two things on the Communication Operator:

1. **Pool CR + broker secret** — the operator creates a `CommunicationPool`
   custom resource and a broker-credentials `Secret` in the kind cluster
   (steps 2–6 below).
2. **Helm-based workload deploys** — for every Adapter / Application
   managed by the pool, the operator runs `helm upgrade --install` (step 7).

Undeploying tears all of that down in reverse order.

The test is **manual** — it is not part of CI. Run it after non-trivial
changes in `OperatorHubService`, `CommunicationPoolManager`, `PoolService`
(controller), `WorkloadReconciler`, or any of the SDK / Helm plumbing they
depend on.

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

       …then, for every workload managed by the pool:

[Controller PoolService.DeployManagedWorkloadsAsync]
            ↓
[Controller /operatorHub SignalR push → WorkloadDeployedAsync(WorkloadDeployedDto)]
            ↓
[OperatorHubService.WorkloadDeployedAsync]
            ↓
[WorkloadReconciler.DeployAsync]
            ├─ materialize K8s Secret '{release}-octo-secrets' for IsSecret values
            ├─ helm repo add (alias derived from URL hash) + helm repo update
            └─ helm upgrade --install --atomic '{tenantId}-{workloadName}' {alias}/{chart}
            ↓
[Helm release + chart-defined Deployment / Service / … in 'octo' namespace]  ← helm + kubectl assertions
```

The same path in reverse for `Undeploy Pool` — workloads are
`helm uninstall`-ed first (so the operator can still resolve the pool's
namespace), then the pool CR is removed. **Edge pools** transition state
without any operator notification — they are installed and run by an
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

### 7. Workload deploy via Helm

This step validates the Phase-3 Helm path: the operator runs
`helm upgrade --install` for every Adapter or Application that is
`Manages`-associated with the pool when it is deployed.

#### 7.1 Prerequisites — a chart you can deploy

You need at least one reachable Helm chart. Easiest options:

- **A published meshmakers chart** (e.g. an Adapter or an App chart that
  CI already publishes to GitHub Pages). Both anonymous releases-Pages and
  private dev-Pages (HTTP basic auth — `Username` / `Password` on the
  `HelmRepositoryConfiguration`) work.
- **Any public test chart**, e.g. `https://charts.bitnami.com/bitnami`
  with chart `nginx`. Useful purely to prove that the Helm pipe is alive
  — the chart does not have to know anything about OctoMesh.

The chart URL needs to be reachable **from inside the kind container's
network**. GitHub Pages and `charts.bitnami.com` are reachable through
the standard kind egress (the operator runs on the host, not in-cluster,
so it uses the host's network — this is usually a non-issue with kind).

#### 7.2 Create a `HelmRepositoryConfiguration`

In Refinery Studio: **General → Configuration → New Configuration → Helm
Repository**. Fill in:

| Field | Example |
|---|---|
| Name | `e2e-test-repo` |
| Repository URL | `https://charts.bitnami.com/bitnami` |
| Channel | `stable` (free text — purely informational) |
| Username | _(leave empty for public repos)_ |
| Password | _(leave empty for public repos; if set, stored encrypted at rest via the controller's `encrypt-value` endpoint)_ |

Save. The configuration appears in the General → Configuration list.

#### 7.3 Create an Application bound to the pool

**Communication → Applications → New Application**. Fill in:

| Field | Example |
|---|---|
| Name | `e2e-nginx` |
| Description | "E2E smoke test workload" |
| Pool | `default` (the pool from step 2) |
| Helm Repository | `e2e-test-repo` (the config from step 7.2) |
| Chart Name | `nginx` |
| Chart Version | _(leave empty to use latest, or pin to a known-good version)_ |
| Hostname | _(empty)_ |
| values.yaml | _(empty for the bitnami sanity test, or paste structured values for a real adapter/app chart)_ |
| Value Overrides | _(empty for the smoke test — but if you add a `Secret`-flagged row, validate in step 7.5 that the operator-owned Secret is materialized)_ |

Save. The Application appears in `Communication → Applications` with
`DeploymentState = UNDEPLOYED`.

> Same form, same checks for an Adapter — the Adapter form added the
> Helm-fields surface in Phase-4. Pick whichever entity you have a chart
> for.

#### 7.4 Deploy the pool (now with workloads)

Right-click the pool row from step 2 → **Deploy Pool** → confirm.

The operator log should print the pool-CR creation lines from step 3,
**immediately followed by** one log block per workload:

```
Workload deployed event received: tenant 'e2etest', release 'e2etest-e2e-nginx'
Ensuring helm repo 'r-<hash>' (https://charts.bitnami.com/bitnami)
Running 'helm upgrade --install e2etest-e2e-nginx r-<hash>/nginx --namespace octo --create-namespace --atomic -f <values.yaml> -f <overrides.yaml>'
Helm release 'e2etest-e2e-nginx' deployed successfully
```

The Application row in the Studio refreshes to `DeploymentState = DEPLOYED`.

#### 7.5 Verify the workload in the cluster

```powershell
helm --kube-context kind-kind list -n octo
```

Expected: a release named `e2etest-e2e-nginx` (or however many workloads
the pool manages), `STATUS = deployed`.

```powershell
kubectl --context kind-kind get all -n octo -l app.kubernetes.io/instance=e2etest-e2e-nginx
```

Expected: whatever resources the chart renders (for `bitnami/nginx`:
a `Deployment`, `ReplicaSet`, `Pod`, `Service`). For a meshmakers Adapter
chart, expect the adapter `Deployment` + `Service` + `ConfigMap`. Wait for
pods to reach `Ready 1/1`.

**If the Application has secret-flagged value overrides**, also assert the
operator-owned secret was materialized:

```powershell
kubectl --context kind-kind get secret -n octo e2etest-e2e-nginx-octo-secrets -o yaml
```

Expected: one entry under `data` per secret-flagged override path. The
ciphertext (`enc:v1:…`) has been decrypted by the controller before being
sent to the operator, so the secret's value here is the plaintext that
the chart will consume via `valueFrom: secretKeyRef`.

#### 7.6 Undeploy the pool (workloads come down first)

Right-click the pool → **Undeploy Pool** → confirm.

The operator log should print the workload-uninstall lines **before** the
pool-CR delete lines:

```
Workload undeployed event received: tenant 'e2etest', release 'e2etest-e2e-nginx'
Running 'helm uninstall e2etest-e2e-nginx --namespace octo --ignore-not-found'
Helm release 'e2etest-e2e-nginx' uninstalled
…
Pool undeployed event received: tenant 'e2etest', pool 'default'
Deleting CommunicationPool CR 'e2etest-default' …
```

The ordering matters: the operator removes the workload releases first so
the pool's namespace and broker secret are still resolvable while
`helm uninstall` runs.

#### 7.7 Verify cleanup

```powershell
helm --kube-context kind-kind list -n octo
# expected: no e2etest-* releases

kubectl --context kind-kind get all -n octo -l app.kubernetes.io/instance=e2etest-e2e-nginx
# expected: No resources found

kubectl --context kind-kind get secret -n octo e2etest-e2e-nginx-octo-secrets
# expected: NotFound — the operator-owned secret is removed alongside the helm release
```

### 8. Bonus — tenant-delete cascade

If you delete the `e2etest` tenant from the OctoSystem context while it
still has a deployed Cloud pool, the controller's `TenantManagementConsumer`
calls `PoolService.UndeployAllCloudPoolsAsync(tenantId)` in the
`PreDeleteTenant` consumer. That fires a `WorkloadUndeployedAsync` event
for every tracked workload, then a `PoolUndeployedAsync` event for every
Cloud pool of the tenant, so the operator cleans up its Helm releases,
CRs, and secrets before the tenant data is gone.

To verify: redeploy the pool (step 3) and at least one workload
(steps 7.3 + 7.4), then from the OctoSystem CLI context:

```powershell
Invoke-OctoCliLoginLocal -tenantId OctoSystem
octo-cli -c Delete -tid e2etest -y
```

Expect the same `helm uninstall …` log lines as in step 7.6 followed by
the `Deleting CommunicationPool CR …` lines from step 5, then an empty
`kubectl get communicationpool -n octo` and an empty
`helm --kube-context kind-kind list -n octo`.

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
| **Workload-deploy step 7.4**: pool CR appears but no `Workload deployed event received` log entry | The Application/Adapter is not associated with the pool. Open the Application form, set **Pool** to the pool you deployed, save, then undeploy and redeploy the pool. The controller fan-out only enumerates workloads connected via the `Manages` association. |
| `HelmException: 'helm repo add' exited with code 1`, stderr mentions `failed to fetch` | The chart repository URL is wrong, requires auth that wasn't provided, or is not reachable from the host. Test from the host: `helm repo add test <url> && helm search repo test`. For a private repo, ensure `Username` / `Password` are set on the `HelmRepositoryConfiguration` (they are stored encrypted; the controller decrypts before pushing on the wire). |
| `HelmException: 'helm upgrade --install' exited with code 1`, stderr mentions `chart "X" matching <version>` not found | Wrong `Chart Name` / `Chart Version` on the Application. Test from the host: `helm search repo <alias>/<chart> --versions`. Leave `Chart Version` empty to grab the latest published version. |
| `helm upgrade --install` succeeds but pods never reach Ready | Chart-side issue — wrong `values.yaml` for the chart, missing dependencies, image-pull failure. Inspect: `kubectl describe pod -n octo -l app.kubernetes.io/instance=<release>` and `kubectl logs -n octo <pod>`. The operator only owns the helm-side; the chart owns runtime correctness. |
| Operator-owned `<release>-octo-secrets` secret is missing despite secret-flagged overrides | The override row in the Application form had `IsSecret = false`, or the value was empty. The reconciler only materializes the secret when at least one override has `IsSecret = true` and a non-empty value. Re-edit, save, redeploy. |

Operator log: stdout of the `start-operator.ps1` terminal. Other services
log to `$rootPath/logFiles/<ServiceName>.log` (managed by `Start-Octo`).
Studio log: stdout of the `npm start` terminal plus the browser dev tools.
