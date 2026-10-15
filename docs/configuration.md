---
description: The four OperatorOptions keys that are easy to get wrong, and why. The full key table lives in README.md.
applies_to: src/CommunicationOperator/Options/**, src/CommunicationOperator/appsettings*.json
---

# Configuration

`OperatorOptions` binds the `Operator` configuration section; every key is also an environment
variable prefixed `OPERATOR__`. **The full table lives in `README.md` -> Configuration - edit it
there, not here.**

Four that are easy to get wrong:

- `WatchNamespace` - empty watches all namespaces. Required when several operators share a cluster,
  or they race on each other's CRs.
- `WorkloadCommunicationControllerUri` - empty projects `CommunicationControllerUri` into workloads.
  Set it only when workloads cannot use the operator's own address: on local kind the operator
  reaches a host-run controller through a pod hostAlias that the adapters do not have, and they
  sit at `Unregistered` while the operator looks healthy.
- `PlatformNamespace` - empty resolves to `DeploymentSiteNamespace`, which is what owner-reference garbage
  collection requires. A distinct namespace disables the owner reference.
- `ClusterDependencies.SystemDatabaseName` / `StreamDataSchemaInstancePrefix` - must mirror the core
  services of the same instance, or workloads fail every CK-model load and read the wrong CrateDB
  schemas. Both empty on a single-instance cluster.
