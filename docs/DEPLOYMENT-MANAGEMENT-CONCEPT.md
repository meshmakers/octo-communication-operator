# Deployment Management Concept

## Overview

The Communication Operator manages Kubernetes deployments for OctoMesh tenants. Beyond mesh adapters, it should also deploy **tenant-specific applications** (customer/third-party apps) with optional ingress and TLS.

This document covers:
1. CK model extension for deployable applications
2. Helm-based deployment strategy
3. Ingress and DNS management
4. Version lifecycle

## Current State

### CK Type Hierarchy (System.Communication)

```
Entity (System)
  └── DeployableEntity (abstract)
        ├── Adapter          — ETL pipeline executor (ImageName, ImageVersion, CommunicationState, ...)
        ├── Pool             — Device group (CommunicationState, ...)
        └── Pipeline         — Data processing definition
```

`DeployableEntity` provides: `DeploymentState`, `Name`, `Description`, `StatusMessage`

`Adapter` adds deployment-specific attributes: `ImageName`, `ImageVersion`

Current `DeploymentState` values: `Undeployed (0)`, `Pending (1)`, `Deployed (2)`, `Error (3)`

### Problem

- `ImageName`/`ImageVersion` are on `Adapter`, not on `DeployableEntity`
- No concept for deploying general applications (only adapters)
- No Helm chart support — operator creates raw K8s Deployments/Services
- No ingress management for deployed workloads

## Proposed CK Model Changes

### New Base Type: DeployableWorkload

Extract the deployment attributes from `Adapter` into a shared base type:

```
Entity (System)
  └── DeployableEntity (abstract)
        ├── DeployableWorkload (abstract, NEW)
        │     ├── Adapter        — ETL pipeline executor
        │     └── Application    — Tenant-specific application (NEW)
        ├── Pool
        └── Pipeline
```

**DeployableWorkload** (new abstract type, inherits DeployableEntity):

| Attribute | Type | Required | Description |
|-----------|------|----------|-------------|
| ImageName | string | yes | Container image name (e.g., `mycompany/energy-dashboard`) |
| ImageVersion | string | yes | Container image tag (e.g., `2.1.0`) |
| HelmChartName | string | yes | Helm chart reference (e.g., `oci://registry/charts/energy-dashboard`) |
| HelmChartVersion | string | yes | Helm chart version (e.g., `1.0.0`) |
| Replicas | int | no | Number of replicas (default: 1) |

**Application** (new concrete type, inherits DeployableWorkload):

| Attribute | Type | Required | Description |
|-----------|------|----------|-------------|
| IngressEnabled | bool | no | Whether to create an Ingress (default: false) |
| IngressName | string | no | DNS prefix for the ingress (e.g., `energy` → `energy.prod-1.octo-mesh.com`) |
| Port | int | no | Container port (default: 80) |
| EnvironmentVariables | string (JSON) | no | JSON object of env vars: `{"API_KEY": "...", "MODE": "production"}` |
| ValuesOverride | string (JSON/YAML) | no | Helm values override (JSON or YAML string) |

**Adapter** changes:
- `ImageName` and `ImageVersion` move to `DeployableWorkload` (breaking change in CK model, but backwards compatible via inheritance)
- Adapter-specific attributes (`CommunicationState`, `Configuration`, etc.) stay on `Adapter`

### Association: Pool → Application

Extend the existing Pool → Adapter "Manages" association pattern:

| Association | From | To | Multiplicity | Description |
|-------------|------|----|-------------|-------------|
| Manages (existing) | Pool | Adapter | N:1 | Pool manages adapters |
| Deploys (new) | Pool | Application | N:1 | Pool deploys applications |

Alternatively, both `Adapter` and `Application` inherit from `DeployableWorkload`, and the "Manages" association is from Pool to DeployableWorkload — one association for both types.

### DeploymentState Extension

Extend the `DeploymentState` enum with a new value for manually installed workloads:

| Value | Code | Meaning | Operator behavior |
|-------|------|---------|-------------------|
| `Undeployed` | 0 | Not deployed, waiting for operator | Operator deploys via Helm |
| `Pending` | 1 | Deployment in progress | Operator is working on it |
| `Deployed` | 2 | Operator deployed and running | Operator manages lifecycle (upgrade, delete) |
| `Error` | 3 | Deployment failed | Operator retries |
| `ManuallyDeployed` | 4 | Installed outside the operator (NEW) | **Operator ignores** — no deploy, upgrade, or delete |

When a workload is set to `ManuallyDeployed`, the operator skips it entirely. This supports:
- **Edge deployments** where adapters are installed via Helm manually
- **Customer-managed adapters** with custom configurations
- **Migration**: Set state from `ManuallyDeployed` to `Undeployed` to hand over to the operator

## Deployment Strategy: Helm Charts

### Why Helm

- Standard packaging format for Kubernetes applications
- Supports templated values (environment-specific configuration)
- Built-in rollback, versioning, release management
- Customer applications likely already have Helm charts
- The operator can use the Helm SDK to install/upgrade/uninstall releases

### Deployment Mode

All workloads (adapters and applications) are deployed via Helm charts. There is no image-only mode — every deployable workload requires a Helm chart reference. This means:

- **Adapters**: The existing `octo-mesh-adapter` Helm chart (from `octo-helm-core`) is used. The operator passes tenant-specific values (tenantId, broker config, adapter IDs).
- **Applications**: Customer-provided or standard Helm charts. The operator passes application-specific values.

This eliminates the dual code path (raw K8s resources vs. Helm) and gives all workloads the same lifecycle: `helm install` → `helm upgrade` → `helm uninstall`.

**Consequence for CK model:** `HelmChartName` and `HelmChartVersion` become **required** attributes on `DeployableWorkload` (not optional).

### Helm Values Generation

The operator generates a Helm values file from the workload attributes:

```yaml
# Auto-generated by Communication Operator
image:
  repository: {{ ImageName }}
  tag: {{ ImageVersion }}
replicaCount: {{ Replicas }}
service:
  port: {{ Port }}
ingress:
  enabled: {{ IngressEnabled }}
  className: nginx
  annotations:
    cert-manager.io/cluster-issuer: {{ clusterIssuer }}
  hosts:
    - host: {{ IngressName }}.{{ clusterDomain }}
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: {{ IngressName }}-tls
      hosts:
        - {{ IngressName }}.{{ clusterDomain }}
env:
  {{ EnvironmentVariables as key-value pairs }}
```

If `ValuesOverride` is set, it is merged on top of the generated values (deep merge, override takes precedence).

### Helm Release Naming

Pattern: `{tenantId}-{applicationName}` (DNS-safe, lowercase)

Example: Tenant `acme-energy`, Application `dashboard` → Helm release `acme-energy-dashboard`

## Ingress and DNS Management

### DNS Pattern

```
{IngressName}.{clusterDomain}
```

Examples:
- Application `energy` on `prod-1.octo-mesh.com` → `energy.prod-1.octo-mesh.com`
- Application `dashboard` on `prod-1.octo-mesh.com` → `dashboard.prod-1.octo-mesh.com`

### TLS Certificate

Handled automatically by cert-manager via the Ingress annotation:
```yaml
cert-manager.io/cluster-issuer: letsencrypt-prod
```

The operator sets this annotation based on the cluster's cert-manager configuration (from OperatorOptions or auto-detected).

### DNS Record

If `external-dns` is deployed (as on prod-1), the DNS record is created automatically from the Ingress host. No manual DNS configuration needed.

### Multi-Tenant Isolation

Each tenant's applications run in a separate namespace or with tenant-scoped labels:
- Namespace: `{tenantId}` or shared namespace with labels
- Labels: `octo-mesh.meshmakers.io/tenant: {tenantId}`
- Network policies can isolate tenant workloads (future)

## Operator Flow

### Application Deployment (via SignalR)

```
1. Admin creates Application entity in OctoMesh (via GraphQL/UI)
   → RtApplication stored in MongoDB with ImageName, HelmChartName, IngressName, etc.

2. Communication Controller notifies connected Operator
   → DeployApplicationAsync(tenantId, ApplicationDto)

3. Operator receives callback:
   - Generate values.yaml from Application attributes
   - helm install/upgrade {tenantId}-{appName} {chartRef} -f values.yaml
   - Ingress is part of the Helm chart (enabled via values when IngressEnabled=true)

4. Operator reports deployment state back to Controller
   → UpdateApplicationDeploymentStateAsync(...)
```

### Application Update

```
1. Admin updates Application entity (e.g., new ImageVersion)
   → Controller sends UpdateApplicationAsync callback

2. Operator: helm upgrade with new values
3. Kubernetes handles rolling update
```

### Application Removal

```
1. Admin deletes Application entity
   → Controller sends UndeployApplicationAsync callback

2. Operator: helm uninstall
```

## Version Management

Extends the adapter version concept to applications:

### Per-Application Versioning

Each Application instance has its own `ImageVersion` / `HelmChartVersion`. Updates are explicit — the admin (or CD pipeline) updates the version in OctoMesh.

### Global Version Update (CD Pipeline)

For OctoMesh-managed applications (e.g., standard adapters), the CD pipeline can trigger a bulk update via the OperatorHub:

```csharp
// IOperatorHubCallbacks extension
Task WorkloadVersionUpdatedAsync(string imageName, string newVersion);
```

The operator updates all workloads (adapters + applications) matching the image name.

### Version Pinning

Applications can have `AutoUpdate: false` to prevent automatic version updates. Only explicit version changes via the OctoMesh API trigger updates.

## Configuration Summary

### OperatorOptions (extended)

| Option | Description | Default |
|--------|-------------|---------|
| `ClusterDomain` | Domain suffix for ingress hostnames | _(required)_ |
| `CertManagerClusterIssuer` | ClusterIssuer name for TLS certificates | `letsencrypt-prod` |
| `DefaultNamespacePattern` | Namespace for tenant workloads (`{tenantId}` or fixed) | `octo-mesh` |
| `HelmRepositoryUrl` | Default Helm repository URL | _(optional)_ |
| `HelmRegistryCredentials` | Credentials for OCI Helm registries | _(optional)_ |

### IOperatorHubCallbacks (extended)

| Callback | Description |
|----------|-------------|
| `TenantCreatedAsync(tenantId)` | Create CommunicationPool CR (existing) |
| `TenantDeletedAsync(tenantId)` | Delete CommunicationPool CR (existing) |
| `DeployApplicationAsync(tenantId, appDto)` | Deploy a tenant application |
| `UpdateApplicationAsync(tenantId, appDto)` | Update a tenant application |
| `UndeployApplicationAsync(tenantId, appDto)` | Remove a tenant application |
| `WorkloadVersionUpdatedAsync(imageName, version)` | Bulk version update |

## Implementation Phases

### Phase 1: CK Model (System.Communication)
- Create `DeployableWorkload` abstract type
- Create `Application` type with ingress/helm attributes
- Refactor `Adapter` to inherit from `DeployableWorkload`
- Migrate `ImageName`/`ImageVersion` from Adapter to DeployableWorkload

### Phase 2: Controller (Communication Controller Services)
- Add `ApplicationService` (CRUD for Application entities)
- Extend `PoolHub`/`OperatorHub` with application deployment callbacks
- Add Application deployment DTOs to SDK contracts

### Phase 3: Operator (Communication Operator)
- Add Helm SDK dependency (`Helm.Sdk` or shell-exec `helm` CLI)
- Implement `ApplicationReconciler` (Helm install/upgrade/uninstall)
- Implement ingress creation with cert-manager and DNS
- Extend `OperatorHubService` to handle application callbacks

### Phase 4: Version Lifecycle
- Add `WorkloadVersionUpdatedAsync` callback
- CD pipeline integration (API endpoint for version announcements)
- Auto-update vs. pinned version support

## Example: Deploying a Customer Energy Dashboard

1. Admin creates Application in OctoMesh for tenant `acme-energy`:
   ```
   Name: energy-dashboard
   ImageName: acme/energy-dashboard
   ImageVersion: 2.1.0
   IngressEnabled: true
   IngressName: energy
   Port: 8080
   EnvironmentVariables: {"API_URL": "https://api.prod-1.octo-mesh.com/acme-energy/v1/graphql"}
   ```

2. Communication Controller notifies Operator via SignalR

3. Operator creates:
   - Deployment: `acme-energy-energy-dashboard` with image `acme/energy-dashboard:2.1.0`
   - Service: ClusterIP on port 8080
   - Ingress: `energy.prod-1.octo-mesh.com` with TLS via cert-manager

4. External-DNS creates DNS record automatically

5. Result: `https://energy.prod-1.octo-mesh.com` is live with valid TLS certificate
