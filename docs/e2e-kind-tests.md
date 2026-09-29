---
description: The kind-cluster suites - adapter pools without helm, scaled redeploy with it - what each proves, how to run them, why they skip.
applies_to: tests/CommunicationOperator.Tests/E2E/**
---

# Kubernetes End-to-End Tests

Two suites, split by whether helm is in the loop. `AdapterPoolKindE2ETests` (first) substitutes it;
`ScaledWorkloadRedeployKindE2ETests` (second) needs the real binary because the defect it proves is
in the apply.

## Adapter pools without helm (AB#4924)

`tests/CommunicationOperator.Tests/E2E/AdapterPoolKindE2ETests` runs the adapter-pool paths against a **real apiserver**: a pool scaled 1 → 3 → 1 through
`WorkloadReconciler.ScaleAsync`, and a pool garbage-collected when its tenant's `DeploymentSite`
CR is deleted. Both prove things a substitute cannot — Kubernetes' garbage collector is a real
controller with real rules, and a merge patch either moves `spec.replicas` on the live object or it
does not.

```bash
OCTO_OPERATOR_E2E_KUBECONTEXT=kind-kind \
  dotnet test --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj \
  -c DebugL -- --treenode-filter "/*/*/AdapterPoolKindE2ETests/*"
```

Without `OCTO_OPERATOR_E2E_KUBECONTEXT` both tests report as **skipped**, never as passed — a green
run on a machine with no cluster would be a lie about what was verified. The context needs the
`deploymentsites.octo-mesh.meshmakers.io` CRD installed (the `octo-mesh-crds` chart) and
permission to create a namespace; everything is created in and cleaned up from `octo-deployment-site-e2e` and
`octo-deployment-site-e2e-platform`.
Helm is deliberately not in the loop: a directly created Deployment carrying the release's
`app.kubernetes.io/instance` label is exactly the shape the scale path selects on.

The manual stack-bringup runbook is a different job: see `docs/E2E-SMOKE-TEST.md`.

## The one suite that does run helm (AB#5350)

`tests/CommunicationOperator.Tests/E2E/ScaledWorkloadRedeployKindE2ETests` is the exception, and the
reason is the defect itself: a server-side-apply field-ownership conflict exists only where there is
an apply, so a substituted helm cannot produce one. Two tests —
`ScaledWorkload_Redeploys_AndStaysAtItsScaledSize` (install → `ScaleAsync` → **reproduce** the
unpinned failure → deploy through the reconciler and require `deployed` with the scaled size intact)
and `HibernatedWorkload_Redeploys_AndStaysDown` (which fails if the hibernation branch stops coming
first, because the live rule declines to pin a live 0).

```bash
OCTO_OPERATOR_E2E_KUBECONTEXT=kind-kind \
  dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj \
  -c DebugL --no-build -- --treenode-filter "/*/*/ScaledWorkloadRedeployKindE2ETests/*"
```

- Needs **helm ≥ 4** on PATH as well as the cluster, and skips otherwise: Helm 3 applies client-side,
  has no notion of field ownership, and would report this defect fixed on a build that still has it.
- Writes its own two-file chart to a temp directory — one Deployment whose `spec.replicas` comes from
  `replicaCount`, which is the entire collision — and wraps the real `HelmRunner` to skip the
  repository registration and point the chart reference at that directory. A repository is how a
  chart is fetched, not how it is applied, so neither substitution touches what is under test.
- Hands the kubectl context to helm through `HELM_KUBECONTEXT` (the reconciler's arguments carry no
  `--kube-context`), and uses `octo-scaled-redeploy-e2e` so it cannot collide with the suite above.
- No CRD needed: it deploys a workload, not a `DeploymentSite`.
