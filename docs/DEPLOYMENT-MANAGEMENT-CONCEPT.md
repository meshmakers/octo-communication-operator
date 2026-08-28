# Helm-Based Workload Deployment

## Status

**Implemented.** The Helm-based deploy flow is the only deploy path for Adapters and Applications; the previous raw-K8s `AdapterReconciler` was removed in System.Communication CK 3.16.0. The Phase-3 E2E smoke test on a real cluster is the remaining validation step.

## Goals

The Communication Operator should deploy two kinds of tenant-scoped workloads to Kubernetes via Helm:

1. **Adapters** — existing concept (ETL pipeline executors that connect back to the controller via SignalR).
2. **Applications** — tenant-specific web apps (energy-community, voest-app, maco-app, …).

Both are packaged as Helm charts hosted on GitHub Pages (classic HTTP Helm repositories — both **public** Pages sites for releases and **private** Pages sites for dev builds, the latter requiring auth). The chart source is configurable per tenant so dev builds and releases can be served from different repositories.

Out of scope for this concept:

- **System-wide variables** for instance-specific values (URIs, secrets). Tracked separately under `project-octo-mesh-variables.md`; deferred until the base Helm-deploy mechanic is in place.
- **External Secret References** (Vault, ESO). Phase 4 / nice-to-have. Initial release uses encrypted-at-rest values in MongoDB.

## CK Model Changes (`System.Communication`)

### New hierarchy

```
Entity (System)
  └── DeployableEntity (abstract, existing)        DeploymentState, Name, Description, StatusMessage
        ├── DeployableWorkload (abstract, NEW)     ChartName, ChartVersion, ValuesYaml,
        │     │                                     Values → List<ValueOverride>,
        │     │                                     HelmRepository → HelmRepositoryConfiguration
        │     ├── Adapter                          (adapter-specific attributes stay)
        │     └── Application (NEW)                Hostname (optional, override default)
        ├── Pool                                   Manages → DeployableWorkload (widen from Adapter)
        └── Pipeline
```

**Removed from `Adapter`:**

- `Deployment.ImageName`
- `Deployment.ImageVersion`

Both move to the Helm chart's `values.yaml` and are no longer first-class CK attributes. **Hard switch** — there is no migration path; the feature was barely used in production.

### New types

**`DeployableWorkload`** (abstract, derives from `DeployableEntity`):

| Attribute | Type | Required | Description |
|---|---|---|---|
| `ChartName` | string | yes | Chart reference path within the registry, e.g. `voest-app`. |
| `ChartVersion` | string | yes | Chart version, e.g. `1.2.3`. Empty means "newest in the repository", resolved by helm at deploy time. Since AB#4955 that resolution happens only on a deploy somebody triggered — a controller-side reconcile of a stranded `Pending` workload keeps the version already installed, so an unrelated platform event cannot change a running application's version. |
| `ValuesYaml` | string (large) | no | Full `values.yaml` content. UI offers a code editor + a file-upload. |
| `Values` | association → `ValueOverride[]` | no | Structured key-path / value list, merged on top of `ValuesYaml`. |
| `HelmRepository` | association → `HelmRepositoryConfiguration` | yes | Which registry / channel to pull the chart from. |

**`Application`** (derives from `DeployableWorkload`, final):

| Attribute | Type | Required | Description |
|---|---|---|---|
| `Hostname` | string | no | Optional override for the public hostname (otherwise the chart's own ingress defaults are used). Operator does not currently manage ingress separately — the chart is expected to declare its own Ingress. |

> The first iteration intentionally keeps `Application` thin. URIs, OAuth client IDs, database connection strings etc. live in `ValuesYaml` / `Values`. Once the Variables feature lands, those become referenceable as `${variable.name}`.

**`HelmRepositoryConfiguration`** (derives from `${System}/Configuration`, final, in `System.Communication`):

| Attribute | Type | Required | Description |
|---|---|---|---|
| `RepositoryUrl` | string | yes | HTTP(S) URL of a Helm repository index, e.g. `https://meshmakers.github.io/octo-helm-core` (public) or a private GitHub Pages site. |
| `Channel` | enum `HelmChannel` | yes | `Dev` or `Release`. Pure label; the operator does not derive behavior from it, but the UI uses it to colour-code. |
| `Username` | string | no | Optional basic-auth username (for private GH Pages — typically a GitHub username or PAT-bearer name). |
| `Password` | string (secret) | no | Optional basic-auth password / PAT. Encrypted at rest (see Secrets section). |

Tenant-scoped, like every other `*Configuration` subtype today. Workloads in tenant `acme` pick from `acme`'s repositories.

### New record

**`ValueOverride`** (CK record):

| Field | Type | Description |
|---|---|---|
| `Path` | string | Helm dotted-path, e.g. `image.tag`, `service.port`, `oauth.clientId`. |
| `Value` | string | The override value (always a string in storage; Helm coerces). |
| `IsSecret` | boolean | If true, `Value` is encrypted at rest and rendered as a Kubernetes Secret reference at deploy time. |

### New enum

**`HelmChannel`** — `Dev (0)`, `Release (1)`.

### Pool ↔ Workload association

`Pool` currently has `Manages → Adapter`. We widen the target to `DeployableWorkload` so a Pool can manage both Adapters and Applications.

```yaml
# types/pool.yaml
associations:
- id: ${this}/Manages
  targetCkTypeId: ${this}/DeployableWorkload  # was: ${this}/Adapter
```

CK runtime supports polymorphic associations — listing a Pool's managed workloads returns a mixed collection of `RtAdapter` + `RtApplication`. Consumers (Studio, Operator) discriminate by `ckTypeId`.

### Model version bump

`System.Communication` goes from `3.14.0` → `3.15.0`. All consumers (`octo-sdk`, controller, operator, studio) get rebuilt.

## Secrets — Phase-1 Approach (Encrypted at Rest)

Every `ValueOverride` with `IsSecret = true` is stored encrypted in MongoDB. Same mechanism is reused for any other CK attribute that needs at-rest encryption in the future (e.g. configuration passwords, OAuth client secrets) — the key is **not** Helm-specific.

The encryption is symmetric (AES-256-GCM) with a master key supplied to the controller via configuration. Reference name: **`InstanceSecretKey`** (one shared symmetric key per OctoMesh instance, scoped to a deployment of the controller).

**Encryption boundary:** controller-side. The controller encrypts on write, decrypts only at the moment it ships values down to the operator over SignalR (which is TLS).

**Master-key delivery:**

- Local dev: `OCTO_INSTANCESECRETKEY` environment variable (base64-encoded 32-byte key).
- Production: K8s Secret mounted into the controller. Recommended path: external Vault `meshmakers/{cluster}/instance-secret-key` → synced via the existing infrastructure pipeline.

The **operator does not need the key** — values arrive already decrypted over the SignalR channel (which is TLS-secured). This keeps the trust boundary clean: the key lives only where the data lives (controller + DB).

**UI behaviour:**

- Secret fields are write-only. After save, the UI shows `••••••••` and an "Update" button that opens a separate input.
- A "Reveal" button can be added later (after permission gating) — not in v1.

**Operator behaviour at deploy time:**

For every `ValueOverride { IsSecret: true }`:

1. Controller already decrypted the value just before sending it via SignalR.
2. Operator collects all secret overrides into a single Kubernetes `Secret` named `{releaseName}-octo-secrets`.
3. Operator rewrites the Helm value at `Path` to a placeholder that the chart resolves via `secretKeyRef` — convention: the chart's value at `Path` must accept either an inline string or `{ valueFrom: { secretKeyRef: { name, key } } }`.

This shifts a small contract requirement onto every chart we deploy: secret-bearing values must be `valueFrom`-aware. Acceptable, because both energy-community / voest / maco charts are ours.

> Phase 4 (with Variables): add a `vault://...` reference syntax so `ValueOverride.Value` can be a pointer instead of an encrypted blob.

## Helm Engine

Choice: **shell-exec `helm` CLI** from the operator container, not a .NET Helm SDK.

Why:

- No production-grade Helm SDK for .NET exists (only Go SDK).
- `helm` CLI is small, well-maintained, and supports OCI + HTTP repos natively.
- Operator already runs as a single container — adding `helm` to the image is trivial.
- Easy to debug: same commands an SRE would type by hand.

Operator wraps `helm` with a thin abstraction (`IHelmRunner`) so tests can substitute it. The runner invokes:

- `helm repo add {alias} {repositoryUrl} [--username --password]` (once per `HelmRepositoryConfiguration`; alias derived from the configuration's RtId)
- `helm repo update {alias}` (before every deploy, to pick up new chart versions)
- `helm upgrade --install {release} {alias}/{chartName} --version {v} -f values.yaml --namespace {ns}`
- `helm uninstall {release} --namespace {ns}`

For private GitHub Pages, `Username` + `Password` flow into `--username` / `--password`. GitHub Pages basic-auth typically wants a username + a PAT with `repo` scope.

## Operator Flow

### Workload Deploy (Adapter or Application)

```
1. User creates / updates an Adapter or Application entity in OctoMesh (Studio GraphQL).
   The entity belongs to a Pool. The Pool has Environment = Cloud.

2. User clicks "Deploy Pool" (existing flow) OR explicitly deploys a single workload
   (new: "Deploy Workload" context menu action).

3. Controller PoolService.DeployPoolAsync:
   - Sets DeploymentState=Deployed on the Pool (existing).
   - Enumerates managed workloads of the Pool.
   - For each Cloud workload, sends WorkloadDeployedAsync(tenantId, workloadDto)
     to connected operators via OperatorConnectionManager. The DTO carries:
       * tenantId, poolName, workloadName, workloadType (Adapter|Application)
       * chartName, chartVersion, registryUri (resolved from HelmRepositoryConfiguration)
       * valuesYaml, valueOverrides (with secrets already decrypted server-side)

4. Operator receives WorkloadDeployedAsync:
   - Logs in to the OCI registry if credentials are present.
   - Writes effective values to a temp file (Yaml merged with overrides, secrets
     swapped for secretKeyRef placeholders).
   - Creates the {release}-octo-secrets K8s Secret if any IsSecret values exist.
   - helm upgrade --install {tenant}-{workloadName} {chart} --version {v} -f values.yaml
       --namespace {poolNamespace}.
   - Reports back via PoolHub.UpdateWorkloadDeploymentStateAsync(workloadRtId, state).

5. Studio refetches the pool, the workload shows DeploymentState = Deployed.
```

### Workload Undeploy

Mirror of deploy. Operator runs `helm uninstall {release}` and `kubectl delete secret {release}-octo-secrets` (if it existed).

### Tenant Delete Cascade

Already in place from the previous fix — the controller's
`UndeployAllCloudPoolsAsync` notifies the operator. New: when the pool is
torn down, the operator additionally enumerates all managed workloads of
the pool and `helm uninstall`s each.

## Implementation Phases

Five phases. Each phase is committable on its own and leaves the system in a working state (the next phase builds on it but doesn't require backporting).

### Phase 1 — CK Model + SDK Contracts

**Repos:** `octo-communication-controller-services` (CK model), `octo-sdk` (DTOs).

1. `System.Communication-3.15.0`:
   - New enum `HelmChannel`.
   - New attributes: `ChartName`, `ChartVersion`, `ValuesYaml`, `RegistryUri`, `Hostname`, `IsSecret`, `Path`, `Value`, `Channel`.
   - New record `ValueOverride`.
   - New types: `HelmRepositoryConfiguration`, `DeployableWorkload`, `Application`.
   - Refactor `Adapter`: now derives from `DeployableWorkload`, drops `ImageName` / `ImageVersion`.
   - Widen `Pool.Manages` to target `DeployableWorkload`.
2. SDK DTOs:
   - `WorkloadDeployedDto`, `WorkloadUndeployedDto` (carry chart + values).
   - `ValueOverrideDto` (`{ Path, Value, IsSecret }`).
   - Extend `IOperatorHubCallbacks` with `WorkloadDeployedAsync` / `WorkloadUndeployedAsync`.
3. Build + push.

**Exit criteria:** All consumers compile against new model; no UI yet. Existing pools/adapters still deploy via the old path (no Helm code yet → operator stubs the new callbacks).

### Phase 2 — Controller-side: HelmRepository CRUD + Encryption

**Repo:** `octo-communication-controller-services`.

1. `HelmRepositoryService` (CRUD over GraphQL/REST).
2. `WorkloadEncryptionService` — AES-256-GCM, master key from `OCTO_HELM_SECRET_KEY`.
3. `ApplicationService` (CRUD over GraphQL).
4. Extend `PoolService.DeployPoolAsync` to walk managed workloads, build per-workload DTOs, decrypt secrets, fire `WorkloadDeployedAsync`.
5. Tests: encryption round-trip, workload DTO assembly.

**Exit criteria:** Operator receives `WorkloadDeployedAsync` calls with fully-resolved values; operator still stubs the actual helm work.

### Phase 3 — Operator-side: Helm Reconciliation

**Repo:** `octo-communication-operator`.

1. Bake `helm` binary into the operator Dockerfile. ✅
2. `IHelmRunner` + `HelmRunner` (process-exec wrapper). ✅
3. `WorkloadReconciler` — same lifecycle (deploy / upgrade / delete) but speaks Helm. ✅
4. Secret materialization: build the `{release}-octo-secrets` K8s Secret from `IsSecret`-flagged overrides; rewrite `valuesYaml` references on the fly. ✅
5. Hook into the existing operator-hub callbacks so the reconciler triggers on `WorkloadDeployedAsync` / `WorkloadUndeployedAsync`. ✅
6. The old `AdapterReconciler` raw-K8s code path has been removed. ✅
7. E2E run-through: kind cluster, OCI registry (use `ghcr.io` test repo), validate one Adapter chart + one Application chart deploys.

**Exit criteria:** Pool deploy / undeploy works end-to-end via Helm. `kubectl get all -n octo` shows Helm-released resources, not operator-built ones.

### Phase 4 — Studio UI

**Repo:** `octo-frontend-refinery-studio`.

1. `HelmRepositoryConfiguration` form (analogous to existing `*ConfigurationForm`s).
2. `Application` list + form. Form has:
   - Basic fields (Name, Description, Pool, HelmRepository, ChartName, ChartVersion).
   - `ValuesYaml` editor (monaco / codemirror, YAML mode + file upload).
   - `ValueOverrides` table editor (Path / Value / IsSecret column; rows addable/removable).
   - Secret fields rendered as `••••••••` after save.
3. Adapter form: drop `ImageName` / `ImageVersion`, add the same Helm fields shared with Application (component reuse).
4. Pool list: existing Deployment column already shows aggregate state.

**Exit criteria:** Full create / edit / deploy / undeploy / delete workflow from the UI for both Adapters and Applications.

### Phase 5 — Documentation + Smoke Test

1. Update `octo-communication-operator/docs/E2E-SMOKE-TEST.md` to cover the Helm deploy flow.
2. Update per-repo CLAUDE.md files (CK model section, Operator reconciler section, Studio form patterns).
3. Add a runbook: "How to publish a new Helm chart version and roll it out."

## Open Questions / TODOs

- **HelmRepositoryConfiguration scoping** is tenant-scoped per the decision above. When the Variables feature lands, we'll add a system-scope config that tenants can opt into instead of providing their own.
- **Chart contract for secrets**: every chart we deploy must support `secretKeyRef` for secret-flagged values. Captured separately in `project-helm-chart-secret-contract.md` (memory) — TODO before the Phase-3 E2E test.
- **Multiple replicas of the operator**: tracking of `OperatorConnectionManager.GetDeployedPools()` is process-local. Pre-existing TODO; not introduced by this refactor.

## References

- Previous draft of this doc (raw-K8s deployment) was replaced 2026-05-11 to reflect the Helm-based direction.
- Related memory: `project-octo-mesh-variables.md` (instance-scoped variables, deferred).
- Related memory: `project-communication-operator-status.md` (E2E passed 2026-05-11).
