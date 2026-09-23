---
description: The automated kind-cluster tests for the adapter-pool paths - what they prove, how to run them, and why they skip without a cluster.
applies_to: tests/CommunicationOperator.Tests/E2E/**
---

# Kubernetes End-to-End Tests

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
