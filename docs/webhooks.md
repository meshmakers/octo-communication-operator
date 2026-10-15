---
description: What the admission webhooks enforce on a DeploymentSite CR, and why the rtId, not a name, is the canonical identity.
applies_to: src/CommunicationOperator/Webhooks/**, src/CommunicationOperator/Entities/**
---

# Webhooks

- `DeploymentSiteValidator`: `Spec.DeploymentSiteRtId` must be a 24-character lowercase hex MongoDB
  ObjectId. The CR carries no display name (that lives on the controller's `RtDeploymentSite.Name`);
  the rtId is the canonical identity and every derived k8s name is built from it via `K8sNaming.DnsName`.
- `DeploymentSiteMutator` is a no-op (`NoChanges()`).

An empty or malformed `DeploymentSiteRtId` would otherwise surface only as a hub-side `FormatException` from
the controller's `OperatorHub.RegisterDeploymentSiteAsync`. `DeploymentSiteController.ReconcileAsync` catches
it, writes `CommunicationStatus = "Failed: <message>"` on the CR and requeues the reconcile every minute, so
the failure is visible but remote and retried forever. Validating at admission rejects the CR up front and
turns a confusing remote failure into a clear local one.
