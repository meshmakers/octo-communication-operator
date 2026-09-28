---
description: Helm 4 field ownership - why a hand patch or the operator's own scale blocks the next deploy, the replica-count pin, ForceConflicts.
applies_to: src/CommunicationOperator/Helm/**, src/CommunicationOperator/Reconcilers/WorkloadReconciler.cs, src/CommunicationOperator/Services/*KubernetesGateway.cs
---

# Helm Field Ownership (AB#5325, AB#5350)

Helm 4 applies server-side, so a field owned by another manager is not merged but refused. Two
defects came from that, one with a person involved and one without; the pre-flight, the scale verb
and the adapter-pool rules in `reconcilers.md` all reference the two sections below.

## A Hand-Patched Workload Locks the Operator Out (AB#5325)

Helm 4 applies **server-side** by default (`--server-side auto`), so a field another manager owns is
not merged — the apply is refused. The only way a foreign manager appears on a workload this operator
deploys is a person writing to the object directly: `kubectl set image`, `kubectl set env` and
`kubectl patch` all leave a manager called `kubectl-set` / `kubectl-patch` behind.

🔴 **It is not transient, and a retry makes it worse.** The ownership lives in the object's
`managedFields`, so it is there on every later deploy — and helm's own rollback applies the same way
and fails for the same reason, which leaves the release in `failed` state. Observed twice on
2026-09-23 on the local kind cluster, on the operator's own Deployment (`kubectl set image`, 16.9.)
and on an adapter pool member (`kubectl set env`, 20.9.), both legitimate stop-gaps at the time:

```
UPGRADE FAILED: conflict occurred while applying object octo/accounting-…aa01 apps/v1,
Kind=Deployment: Apply failed with 1 conflict: conflict with "kubectl-set" using apps/v1:
.spec.template.spec.containers[name="mesh-adapter"].env[name="OCTO_SYSTEM__DATABASEHOST"].value
```

Two pieces:

- **`Helm/HelmFieldOwnershipConflict`** recognises the shape and answers with the cause and both ways
  out. `WorkloadReconciler.DeployAsync` catches it in an exception filter **before** the diagnostics
  path, because that path has nothing to contribute here — no pod was ever created, so there are no
  events to collect and the ten-second budget would be spent proving it. The explanation **replaces**
  helm's stderr on the way to the workload's `LastDeploymentError` (helm's own account repeats the
  conflict three times and names no remedy); the original is on the log line above it, with its stack.
- **`OperatorOptions.Helm.ForceConflicts`** (`OPERATOR__HELM__FORCECONFLICTS`, default **false**) adds
  `--force-conflicts` so a later deploy takes ownership instead of refusing. Off by default on
  purpose: it silently overwrites whatever a person set by hand, on every deploy from then on — an
  operator may well want that, but it has to be their call. **Never** applied to the `--dry-run=server`
  pre-flight, which asks "would this apply"; forcing its way past a conflict there answers a question
  nobody asked. There is no chart value for it — the env var is the surface, deliberately, because
  this is a lever someone reaches for during an incident and not a deployment-time setting.

The other remedy needs no option and is usually the right one: delete the Deployment and let the
chart own it again. Both are named in the error.

⚠️ **A conflict with helm's own manager is not a hand patch** and is deliberately not reported as one
— it would send an operator looking for a person who was never there.

🔴 **Neither is a conflict with `unknown` or with the operator itself — and the first live run of this
code got that wrong.** It told an operator that someone had patched a field by hand that the
**operator's own scale verb** had written: `ScaleDeploymentsByInstanceAsync` merge-patched
`spec.replicas` without a `fieldManager`, so Kubernetes recorded the writer as `unknown`. Two
consequences, both handled:

- The patch now writes as `octo-communication-operator`
  (`DeploymentSiteKubernetesGateway.FieldManagerName`), so the write is attributable at all.
- The explanation distinguishes three writers: the operator's own out-of-band write, a bare `unknown`
  (which it spells out as "the name Kubernetes records when a writer sets no field manager" and
  attributes to a past scale rather than to a person), and a `kubectl-*` manager, which is the only
  one it calls a hand-written change.

The **conflict** that scale causes was a separate defect, fixed in
[A Scaled Workload Fails Its Next Deploy (AB#5350)](#a-scaled-workload-fails-its-next-deploy-ab5350)
below: `spec.replicas` is rendered by the chart, so helm wanted the field the scale verb owns and
every scaled workload failed its next deploy. The deploy now pins `--set replicaCount=<live>` so the
values agree instead of forcing them apart.

⚠️ **The `--dry-run=server` pre-flight cannot see an ownership conflict at all** — measured
2026-09-24 on the kind cluster: with the live object owned by `octo-communication-operator` at a
different replica count, `helm upgrade --install --dry-run=server` exits **0**, while
`kubectl apply --server-side --dry-run=server` of the *same rendered manifest* reports the conflict.
So helm's dry run does not apply server-side, every ownership conflict lands on the real install, and
keeping `--force-conflicts` off the pre-flight is a statement of intent rather than something that
changes an outcome.

Tests: `Helm/HelmFieldOwnershipConflictTests` — 🔴 driven by **verbatim** helm stderr, captured from
both incidents *and from the first live run of this code*, which is what caught four separate parser
and wording bugs no invented sample did: helm embeds the
failure in a quoted `error="…"` field, so the field paths arrive with their quotes **escaped**, and a
character class without the backslash cut every path off at `containers[name=` — after
de-duplication they all collapsed into one and the message named no field at all. The live run then
added three more: stripping *every* backslash glued the following word onto a path (helm carries its
line breaks as the two characters `\n`, giving `…DATABASEHOST"].valuenconflicts`); trailing `"` and
`:` made one field look like three; and the **manager** pattern accepted only bare quotes, so it
reported one manager of two — the second conflict existed only in the escaped copy. Plus the plural
`conflicts with` / bulleted shape the rollback produces, several managers on one object, the three
writer kinds above, and that every unrelated helm failure still falls through to the diagnostics
path. Flag wiring in `Helm/HelmRunnerTests` (default off, on when set, never on the dry run).

⚠️ **The lesson is not "use real stderr" but "use real stderr from the path you changed".** The two
incident samples were real and still missed everything above, because helm reports the same failure
twice — once inside a quoted `error="…"` field and once unquoted — and which copy carries which
conflict depends on the run.

## A Scaled Workload Fails Its Next Deploy (AB#5350)

The operator's **own** scale verb locked it out of every later deploy, for the same server-side-apply
reason as AB#5325 but with no person involved. `ScaleDeploymentsByInstanceAsync` merge-patches
`spec.replicas` outside helm (AB#4917, deliberately — no helm run, no release-history churn), and the
chart renders `spec.replicas` from `replicaCount`. So helm wants exactly the field the scale patch
owns, and Helm 4 refuses the apply. Measured on the kind cluster, 2026-09-24:

```
$ kubectl -n octo-ab5350 patch deploy ab5350 --type=merge \
    -p '{"spec":{"replicas":3}}' --field-manager=octo-communication-operator
$ helm upgrade --install ab5350 ./chart -n octo-ab5350 --rollback-on-failure
level=WARN msg="upgrade failed" error="… Apply failed with 1 conflict: conflict with
  \"octo-communication-operator\" using apps/v1: .spec.replicas"
level=WARN msg="Rollback \"ab5350\" failed: … conflict with \"octo-communication-operator\" …"
Error: UPGRADE FAILED: an error occurred while rolling back the release. …
```

Same object, pinned at what it is actually running:

```
$ helm upgrade --install ab5350 ./chart -n octo-ab5350 --rollback-on-failure --set replicaCount=3
STATUS: deployed          # spec.replicas still 3, helm now co-owns f:replicas
```

🔴 **The rule is agreement, not force.** Server-side apply reports a conflict only for a
foreign-owned field the applier would *change*; applying the value that is already there is accepted
and makes helm a co-owner. `--force-conflicts` (AB#5325) would also get past it, but by overwriting —
and it is off by default for good reasons that have nothing to do with this.

⚠️ **The pin is needed on every deploy, not once.** Co-ownership does not survive the next scale: a
merge patch is an `Update`, which takes the field back, and the deploy after that conflicts again
(measured). And a scale that does not change the value registers no ownership at all — the apiserver
skips the write entirely (`deployment patched (no change)`), so it cannot cause a conflict either.

`WorkloadReconciler.ResolveReplicaCountPinAsync` is the single decision, and the AB#4917 hibernation
pin is its first branch rather than a competing `--set` (two `--set replicaCount=` on one command
line would be resolved by argument order, which is not a rule anyone can read off the code). It feeds
both the `--dry-run=server` pre-flight and the real install, so the pre-flight validates the values
that actually get applied:

| Case | Pin | Why |
|---|---|---|
| `Hibernated` | `0` | The controller's lifecycle state machine has the workload down; a redeploy must not resurrect it, whatever is live. |
| live count > 0 | the live count | The value the object already has; the apply then changes nothing on that field. |
| no Deployments | none | First install (or an undeployed release): nothing is running, nothing owns the field, so the chart and the values layers are the only opinion in existence. |
| several Deployments disagreeing | the **largest**, with a warning | `replicaCount` is one value for the whole release, so one number has to be chosen. The smallest would scale the busiest Deployment down as a side effect of an unrelated deploy — the accident this pin exists to stop. A release with several Deployments rendered from one `replicaCount` is not the shape the scale verb was built for, hence the warning. |
| live count is 0, **not** hibernated | none | Pinning 0 would make every future deploy the thing that keeps the workload down, and no deploy could undo it. Leaving the conflict in place for this case is visible, explained, and recoverable — the cheaper failure. |
| read failed | none | Best effort, like every other cluster-state read on this path: the deploy proceeds with pre-AB#5350 behaviour, because recovering the workload matters more than pinning it. |

`IDeploymentSiteKubernetesGateway.GetDeploymentReplicasByInstanceAsync` does the read, with the
**same** `app.kubernetes.io/instance={release}` selector the scale path patches through — a read that
asked anything else (a pool lives in `PlatformNamespace`) would find nothing and silently fall back to
"first install", i.e. to the defect. It returns the counts and not a decision: a Deployment whose
`spec.replicas` is unset is skipped rather than reported as the API default of 1, because inventing it
would turn "could not read this" into a pin that changes what is running.

🔴 **Consequence, deliberately accepted: once a workload is running, a helm deploy no longer decides
its replica count.** That is the point, and it is also what the controller already claims to do — it
sends `replicaCount = MinReplicas` for an adapter pool on the grounds that "everything above that is a
scaling decision … so a scaled-up pool is not reverted by the next unrelated reconcile"
(`DeploymentSiteService.AppendAdapterPoolMemberOverrides`), which before this fix it *was*, and since
Helm 4 could not even be. An **adapter pool** therefore keeps the size its lease scheduler scaled it
to; `MinReplicas`/`MaxReplicas` are enforced where scale requests are (controller-side
`WorkloadLifecycleService` clamps every request into the range), which is one place rather than two
that can disagree. Same reasoning as AB#4955 for the chart version and AB#5301 for `LifecycleMode`: a
replica count is runtime state, and a deploy that silently resets runtime state is the defect.

⚠️ **One case agreement cannot fix, by construction:** a deploy that genuinely has to *change* the
field — `Hibernated` set while replicas are still above 0, i.e. a deploy landing inside the
`Draining → scale 0 → Hibernated` window — still conflicts (measured). It is transient and
self-correcting: the drain finishes in seconds, and the deploy after that lands on a release that is
already at 0 and applies cleanly. The other side of the same coin is that `spec.replicas` written by
*anyone* is now honoured, `kubectl scale` included, so AB#5325's hand-patch diagnosis no longer fires
for that one field.

Tests: `Reconcilers/WorkloadReconcilerTests/ReplicaCountPinTests` (every row of the table, the pin on
both helm calls, the read's namespace/release, hibernation not even asking what is running, and the
cancellation that must not be swallowed) plus the hibernation cases in `DeployAsyncTests`.

🔴 **And `E2E/ScaledWorkloadRedeployKindE2ETests`, which is where the actual defect is proved.**
`AdapterPoolKindE2ETests` substitutes helm on purpose and this defect lives entirely in the helm layer
— with no apply there is no field manager and nothing to reproduce — so a second E2E class runs the
**real** `HelmRunner` over the **real** helm binary against the real apiserver, with a two-file chart
the test writes to a temp directory and a thin wrapper that skips the repository registration and
points the chart reference at it. Those two substitutions are how a chart is *fetched*, not how it is
applied. The test installs, scales through `ScaleAsync`, asserts from `managedFields` that the
operator owns `f:replicas`, **reproduces the failure** (the pre-AB#5350 call: same helm upgrade, no
pin → `HelmException` that `HelmFieldOwnershipConflict` recognises and that names
`.spec.replicas`), then deploys through the reconciler and requires success, `spec.replicas` still 3,
and the release status `deployed` — not merely "did not throw", because a release left `failed` is
what the next deploy trips over. Verified by mutation: with the live-count branch removed, that last
step fails with the conflict. The suite skips unless `OCTO_OPERATOR_E2E_KUBECONTEXT` is set **and**
helm on PATH is ≥ 4 — Helm 3 patches client-side, has no notion of field ownership, and would report
this defect fixed on a build that still has it.
