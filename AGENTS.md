# AGENTS.md

Instructions for coding agents in this repository. Design rationale lives in `docs/`; read the
matching file before changing the code it covers.

## What this repo is

A Kubernetes operator (KubeOps) that reconciles `DeploymentSite` CRs into Helm releases for mesh
Adapters and Applications, and talks to the Communication Controller over the SignalR `/operatorHub`.

- The CRD ships from `octo-helm-core` (`octo-mesh-crds` chart). Do not add or edit CRD YAML in this
  repo; regenerate with the command in `README.md` -> "Generate CRD and deployment files".
- `V1DeploymentSiteEntity`, group `octo-mesh.meshmakers.io`, version `v1`.
- There is no raw-K8s `AdapterReconciler`. Every Adapter and Application is deployed via Helm.
- Central mode (`OPERATOR__AUTOMANAGEDEPLOYMENTSITES=true`) auto-creates and deletes CRs; edge mode (`false`)
  does not. `OPERATOR__COMMUNICATIONCONTROLLERURI` is required in **both** modes - the SignalR
  connection is not gated on `AutoManageDeploymentSites`.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/CommunicationOperator/Options/**`, `src/CommunicationOperator/appsettings*.json` | `docs/configuration.md` |
| `tests/CommunicationOperator.Tests/E2E/**` | `docs/e2e-kind-tests.md` |
| `src/CommunicationOperator/Controller/**`, `src/CommunicationOperator/Program.cs`, `src/CommunicationOperator/Services/*Diagnostics*.cs` | `docs/http-surface.md` |
| `src/CommunicationOperator/Services/**` | `docs/operator-hub.md` |
| `src/CommunicationOperator/Reconcilers/**`, `src/CommunicationOperator/Helm/**` | `docs/reconcilers.md` |
| `tests/**` | `docs/testing-conventions.md` |
| `src/CommunicationOperator/Webhooks/**`, `src/CommunicationOperator/Entities/**` | `docs/webhooks.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md` (CK model and deploy
contract) and `docs/E2E-SMOKE-TEST.md` (the manual runbook; "Before you commit" says when to
run it). The `/operatorHub` contract itself lives in
`octo-communication-controller-services/CLAUDE.md`.

## Build & test

```bash
# DebugL = local development against the monorepo NuGet cache at ../nuget/
dotnet build Octo.CommunicationOperator.sln -c DebugL

# Canonical: same form the Azure Pipeline runs.
# The `--` separates SDK args from Microsoft.Testing.Platform args.
dotnet test --solution Octo.CommunicationOperator.sln -c DebugL -- --report-trx --report-trx-filename test-results.trx

# Quick form during development (no TRX, no build):
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build

# Single test class
dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj -c DebugL --no-build -- \
    --treenode-filter "/*/*/DeploymentSiteValidatorTests/*"
```

`dotnet test` runs through Microsoft.Testing.Platform (`global.json` sets
`"test": { "runner": "Microsoft.Testing.Platform" }`; the VSTest path is rejected on the .NET 10 SDK).
Two consequences when you edit a command:

- Pass the project or solution as a flag (`--project` / `--solution`). A positional argument is
  rejected.
- Put reporter and filter arguments after `--`.

## Before you commit

1. `dotnet build Octo.CommunicationOperator.sln -c DebugL` succeeds with zero warnings
   (`TreatWarningsAsErrors=true`).
2. The canonical test run reports 0 failed:

   ```bash
   dotnet test --solution Octo.CommunicationOperator.sln -c DebugL \
       -- --report-trx --report-trx-filename test-results.trx
   ```

3. Update the docs the change touches:
   - changed a config key -> the table in `README.md`
   - changed reconciler or Helm behaviour -> `docs/reconcilers.md`
   - changed hub, registration or token behaviour -> `docs/operator-hub.md`
   - changed `OperatorHubService` or `DeploymentSiteManager` -> run `docs/E2E-SMOKE-TEST.md`

4. If you touched `AGENTS.md` or any file in `docs/`: `Test-OctoAgentDocs -Fix` reports 0 errors.
   It rewrites the generated routing table, so run it before you stage, not after.

## Rules

- **Do not add `[Authorize]` to a controller in this repo.** `Program.cs` registers no
  authentication scheme, so the attribute makes every request to that endpoint fail with
  `InvalidOperationException: No authenticationScheme was specified`. Gating an endpoint here means
  bringing an authority, an OIDC client and a bearer scheme with it first.
- **Do not add `[ApiVersion]`.** Routes are literal `system/v1/[controller]`; `Asp.Versioning` is not
  wired into this host and the `v1` in the template is the version pin.
- **New HTTP endpoints are gated by remote address, not by attributes.** Copy the loopback check in
  `Controller/DiagnosticsController`: unmap IPv4-mapped IPv6 (`::ffff:127.0.0.1`) before testing, and
  treat a missing remote address as not loopback.
- **Add new Kubernetes calls to `IDeploymentSiteKubernetesGateway`.** Do not call `IKubernetes` or
  its extension methods from anywhere else.
- **Never rethrow out of a SignalR hub callback or a reconciler.** Failures are logged and reported in
  the ack; one bad event must not drop the hub connection.
- **Operator authentication stays optional.** With no `OPERATOR__AUTHENTICATION__CLIENTID` the
  operator connects anonymously, which is what every installation in the estate does today. Do not
  make any of the `Authentication` keys required.
- **Redeploy must not resurrect a hibernated workload** - see `docs/reconcilers.md`, scale verb.
- **`OctoNugetPrivateServer` must reach every MSBuild invocation in `azure-pipelines.yml`** (explicit
  `Restore` -> `Build` -> `Test` steps). `Directory.Build.props` reads it to pick `OctoVersion` and
  `RestoreSources`; a step that misses it falls back to nuget.org and drags in stale transitive
  packages (`NU1902` under `TreatWarningsAsErrors`).
- **Use `command: 'custom'` + `--solution` in the pipeline**, not `command: 'test'` + `projects:` -
  the latter passes a positional glob, which MTP rejects. `--solution` picks up every test project in
  the `.sln`.
