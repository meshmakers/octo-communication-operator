using System.Collections.Concurrent;
using System.Text;
using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Common;
using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Drives the per-workload Helm lifecycle. Owns three side effects on each
/// deploy: the operator-managed Kubernetes <c>Secret</c> for secret-flagged
/// overrides, the Helm chart repository registration, and the
/// <c>helm upgrade --install</c> call itself.
/// </summary>
public sealed class WorkloadReconciler : IWorkloadReconciler
{
    /// <summary>Grace window for atomic rollback after a deploy cancel,
    /// before <see cref="UndeployAsync"/> proceeds with <c>helm uninstall</c>.
    /// Helm needs a moment to mark the release as failed and drop the
    /// release lock; jumping straight to uninstall while the kill is still
    /// being acknowledged would race with the in-flight rollback.</summary>
    internal static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>How old a <c>pending-*</c> release revision must be before its lock is treated
    /// as an orphan and cleared (AB#4894). Must comfortably exceed helm's atomic timeout
    /// (default 5 min) so a legitimately running upgrade — e.g. on the outgoing pod during a
    /// rolling operator upgrade — is never robbed of its lock.</summary>
    internal static TimeSpan StaleHelmLockThreshold { get; set; } = TimeSpan.FromMinutes(10);

    private readonly IHelmRunner _helm;
    private readonly IDeploymentSiteKubernetesGateway _gateway;
    private readonly IWorkloadDiagnosticsCollector _diagnostics;
    private readonly IServiceProvider _serviceProvider;
    private readonly OperatorOptions _options;
    private readonly ILogger<WorkloadReconciler> _logger;

    // Tracks the cancellation source of every in-flight DeployAsync so that
    // a concurrent UndeployAsync for the same release can abort the running
    // helm process instead of serializing behind it (which would otherwise
    // block on helm's --rollback-on-failure wait timeout, typically 5 min).
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightDeploys = new();

    public WorkloadReconciler(IHelmRunner helm, IDeploymentSiteKubernetesGateway gateway,
        IWorkloadDiagnosticsCollector diagnostics,
        IServiceProvider serviceProvider,
        IOptions<OperatorOptions> options, ILogger<WorkloadReconciler> logger)
    {
        _helm = helm;
        _gateway = gateway;
        _diagnostics = diagnostics;
        // IOperatorHubInvoker is resolved lazily to break the DI cycle:
        // OperatorHubService (the IOperatorHubInvoker implementation) depends
        // on IWorkloadReconciler (this class). Constructor-injecting the
        // invoker here would create a singleton-to-singleton cycle. Both
        // services are singletons, so the lazy lookup is cheap and runs
        // only inside DeployAsync where the watcher actually needs it.
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task DeployAsync(WorkloadDeployedDto workload, CancellationToken cancellationToken)
    {
        var release = ReleaseName(workload.TenantId, workload.WorkloadRtId);
        var ns = ResolveNamespace(workload.WorkloadType);
        var secretName = SecretName(release);

        _logger.LogInformation(
            "Deploying workload: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId}), chart '{ChartName}:{ChartVersion}', release '{Release}' in namespace '{Namespace}'",
            workload.TenantId, workload.DeploymentSiteRtId,
            workload.WorkloadName, workload.WorkloadRtId,
            workload.ChartName, workload.ChartVersion, release, ns);

        // Per-deploy cancellation source — linked to the incoming token so
        // upstream shutdown still cancels, but also reachable from
        // UndeployAsync via _inFlightDeploys to abort a stuck install
        // without waiting for helm's atomic timeout.
        using var deployCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deployToken = deployCts.Token;

        // Refuse to start a second deploy for the same release while one is
        // already running. The current DeploymentSiteService path is serial per
        // workload, so this should not happen in practice; the explicit
        // guard turns "what if" into a controlled failure that points at the
        // bug (instead of two helm processes fighting for the same release
        // lock).
        if (!_inFlightDeploys.TryAdd(release, deployCts))
        {
            throw new InvalidOperationException(
                $"A deploy for release '{release}' is already in progress; refusing to start a second one.");
        }

        Task? watcherTask = null;

        try
        {
            // 0. If the workload opts in, append secret-flagged overrides for
            //    the cluster-internal credentials the operator knows about.
            //    The resulting overrides flow through the normal secret-flagged
            //    path: materialized into {release}-octo-secrets, referenced from
            //    the chart via valueFrom secretKeyRef.
            workload = workload with
            {
                Values = AppendClusterSecrets(workload.Values, workload.ReceivesClusterSecrets,
                    workload.WorkloadType, _options),
            };

            // AB#4924: a deployment site's resources belong to the tenant that lends it out, so that deleting
            // the tenant takes the deployment site with it even when the controller-driven undeploy cascade
            // never runs (controller down, operator restarted, tracking lost). Resolved before the
            // secret is written so both the secret and the release's Deployments carry it.
            var ownerReference = await TryResolveDeploymentSiteOwnerReferenceAsync(workload, ns, deployToken);

            // 1. Materialize / refresh the operator-owned secret. We replace it
            //    every deploy so a value rotation propagates without manual
            //    intervention. When no secret-flagged overrides exist, ensure any
            //    leftover secret from a previous deploy is removed.
            await ReconcileSecretAsync(ns, secretName, workload, ownerReference, deployToken);

            // 2. Make sure the chart repository is registered + index refreshed.
            var alias = RepoAlias(workload.RepositoryUrl);
            await _helm.EnsureRepoAsync(alias, workload.RepositoryUrl,
                workload.RepositoryUsername, workload.RepositoryPassword, deployToken);

            // 3. Assemble values files. Helm later args win — so order is:
            //    context (operator-managed cluster defaults) → workload
            //    ValuesYaml → structured overrides. Workload-side input always
            //    has the final say.
            var tempDir = Directory.CreateTempSubdirectory("octo-helm-").FullName;
            try
            {
                var valuesFiles = new List<string>();

                var contextYaml = WorkloadContextValuesBuilder.Build(_options, workload);
                if (!string.IsNullOrEmpty(contextYaml))
                {
                    var contextFile = Path.Combine(tempDir, "values-context.yaml");
                    await File.WriteAllTextAsync(contextFile, contextYaml, deployToken);
                    valuesFiles.Add(contextFile);
                }

                if (!string.IsNullOrWhiteSpace(workload.ValuesYaml))
                {
                    var baseFile = Path.Combine(tempDir, "values-base.yaml");
                    await File.WriteAllTextAsync(baseFile, workload.ValuesYaml, deployToken);
                    valuesFiles.Add(baseFile);
                }

                var overrideYaml = WorkloadOverrideYamlBuilder.Build(workload.Values, secretName);
                if (!string.IsNullOrEmpty(overrideYaml))
                {
                    var overrideFile = Path.Combine(tempDir, "values-overrides.yaml");
                    await File.WriteAllTextAsync(overrideFile, overrideYaml, deployToken);
                    valuesFiles.Add(overrideFile);
                }

                var chartRef = $"{alias}/{workload.ChartName}";
                var setValues = new Dictionary<string, string>();

                // AB#4917: a redeploy of a hibernated workload must not resurrect
                // it. --set beats every -f values layer, so this pins the release
                // at 0 replicas regardless of what the chart or the entity's
                // values declare. A deploy that is supposed to wake the workload
                // goes through the controller's wake gate first, which clears
                // the hibernated state before the deploy event is sent.
                if (workload.Hibernated)
                {
                    setValues["replicaCount"] = "0";
                    _logger.LogInformation(
                        "Workload '{WorkloadName}' (release '{Release}') is hibernated; deploying with replicaCount=0",
                        workload.WorkloadName, release);
                }

                // AB#4894: a helm process killed mid-upgrade (e.g. the operator
                // pod was replaced by a rollout while a deploy was in flight)
                // leaves the newest release revision in a pending-* status. That
                // lock blocks every later install/upgrade/rollback with "another
                // operation is in progress" and never clears itself — the only
                // remedy used to be a manual Undeploy→Deploy cycle. Clear a
                // provably stale lock before the pre-flight runs.
                await TryClearStaleHelmLockAsync(release, ns, deployToken);

                // AB#4955: an empty ChartVersion means "newest in the repository", resolved right
                // here by helm. That is what the user asked for on a deploy they triggered — but a
                // reconciliation is not a release decision, it restores what was already supposed to
                // be running. Resolving anew there let unrelated platform events (an operator
                // restart, a blueprint re-apply) silently move a customer's app to a newer version.
                var chartVersion = await ResolveChartVersionAsync(workload, release, ns, deployToken);

                // Pre-flight: helm upgrade --install --dry-run=server submits the
                // rendered manifests to the apiserver with dryRun=All. This
                // catches schema errors, admission-webhook rejections, RBAC
                // problems and missing required values in under 2s — instead of
                // letting the real install wait 5min for the atomic timeout
                // before reporting them as a generic "context deadline exceeded".
                // ImagePull / CrashLoop / probe failures are NOT caught here
                // (no pods are created) — those are surfaced by the post-failure
                // diagnostic collector below AND by the live watcher started
                // before the real install.
                await _helm.UpgradeInstallDryRunAsync(release, chartRef,
                    chartVersion, ns, valuesFiles, setValues, deployToken);

                // Start the live watcher BEFORE the real install begins. The
                // watcher polls the cluster every few seconds and pushes any
                // non-empty diagnostic snapshot to the controller so the UI
                // can show ImagePullBackOff (or similar) within seconds
                // instead of waiting for helm's atomic timeout to elapse.
                // The watcher shares the deploy's cancellation token so it
                // is automatically torn down when the deploy ends (success,
                // failure or external cancel).
                var hub = _serviceProvider.GetRequiredService<IOperatorHubInvoker>();
                // Shared cutoff for both diagnostics consumers below — the live watcher and the
                // post-failure snapshot. Captured before helm starts so nothing this deploy
                // produces is missed, and nothing the previous hour produced is re-reported.
                var deployStartedUtc = DateTime.UtcNow;
                watcherTask = WorkloadDeployWatcher.RunAsync(
                    _diagnostics, hub, ns, release, workload, _logger, deployToken);

                try
                {
                    await _helm.UpgradeInstallAsync(release, chartRef,
                        chartVersion, ns, valuesFiles, setValues, deployToken);
                    // Only after the install: the Deployments the reference is written to are
                    // created by helm, so there is nothing to stamp before this point.
                    await ApplyDeploymentSiteOwnerReferenceAsync(release, ns, ownerReference, deployToken);
                }
                catch (HelmException ex) when (HelmFieldOwnershipConflict.IsFieldOwnershipConflict(ex.StdErr))
                {
                    // 🔴 AB#5325 — a field-ownership conflict, handled before the diagnostics path
                    // below, because that path has nothing to contribute here: no pod was ever
                    // created, so there are no events to collect and the ten-second budget would be
                    // spent proving that. What the operator needs is the cause and the way out.
                    var explanation = HelmFieldOwnershipConflict.Explain(release, ns, ex.StdErr);
                    _logger.LogError(ex, "Workload '{Release}' deploy refused by server-side apply. {Explanation}",
                        release, explanation);

                    // The explanation REPLACES helm's stderr rather than being appended to it: this
                    // text lands on the workload's LastDeploymentError, and helm's own account is a
                    // wall of rollback noise that repeats the conflict three times and names no
                    // remedy. The original is on the log line above, with its stack.
                    throw new HelmException(ex.Operation, ex.ExitCode, ex.StdOut, explanation);
                }
                catch (HelmException ex)
                {
                    // --rollback-on-failure leaves only an opaque helm-side error
                    // on its stderr (typically "context deadline exceeded"); the
                    // actual pod-level root cause is observable on the
                    // cluster but vanishes when helm rolls the release
                    // back. Events outlive the pods that produced them
                    // (default TTL 1h), so a post-failure snapshot still
                    // catches ImagePull / scheduling / mount errors — bounded
                    // to THIS deploy, or that same TTL would hand back the
                    // previous hour's failures as if they were current. Use a
                    // bounded token so a stuck apiserver doesn't make the
                    // failure path hang forever.
                    using var diagCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    string diagnostics;
                    try
                    {
                        diagnostics = await _diagnostics.CollectAsync(ns, release, deployStartedUtc, diagCts.Token);
                    }
                    catch (Exception diagEx)
                    {
                        _logger.LogWarning(diagEx, "Failed to collect diagnostics for release '{Release}'", release);
                        throw;
                    }

                    if (string.IsNullOrEmpty(diagnostics))
                    {
                        throw;
                    }

                    _logger.LogError("Workload '{Release}' deploy failed. Root-cause diagnostics:\n{Diagnostics}",
                        release, diagnostics);
                    throw new HelmException(ex.Operation, ex.ExitCode, ex.StdOut,
                        $"{ex.StdErr}\n\nPod diagnostics:\n{diagnostics}");
                }
            }
            finally
            {
                // Best-effort cleanup; if it fails it's not fatal — the values
                // contain decrypted secrets, but the directory is in the
                // operator's per-container tmp.
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove temp values directory '{Path}'", tempDir);
                }
            }
        }
        finally
        {
            // Cancel + drain the watcher first. The deploy is finished one
            // way or another at this point, so we don't want any more
            // progress reports racing with the terminal status report that
            // OperatorHubService.WorkloadDeployedAsync writes right after.
            deployCts.Cancel();
            if (watcherTask is not null)
            {
                try
                {
                    await watcherTask;
                }
                catch
                {
                    // Watcher catches its own exceptions; reaching here means
                    // a swallow path was missed — log but don't propagate, the
                    // deploy outcome is already being reported.
                }
            }

            _inFlightDeploys.TryRemove(release, out _);
        }
    }

    /// <summary>
    /// Decides which chart version this deploy runs with (AB#4955).
    ///
    /// A pinned <c>ChartVersion</c> is always honoured, and so is an empty one on a deploy a human
    /// triggered — there "newest in the repository" is the request. The one case that needs a
    /// different answer is an unpinned workload on a <see cref="WorkloadDeployedDto.IsReconciliation"/>
    /// dispatch: that is the controller restoring what was supposed to be running (AB#4894), so it
    /// must land on the version already installed rather than on whatever happens to be newest at
    /// that moment. Reading it back from the installed release keeps "latest" meaning "latest when
    /// somebody deployed", not "latest whenever a pod restarts".
    ///
    /// Falls back to the workload's own (empty) version whenever there is nothing to read — a
    /// reconcile for a release that was never installed is a first install and legitimately resolves
    /// to newest.
    /// </summary>
    private async Task<string> ResolveChartVersionAsync(WorkloadDeployedDto workload, string release, string ns,
        CancellationToken cancellationToken)
    {
        if (!workload.IsReconciliation || !string.IsNullOrWhiteSpace(workload.ChartVersion))
        {
            return workload.ChartVersion;
        }

        string? installed;
        try
        {
            installed = await _helm.GetInstalledChartVersionAsync(release, workload.ChartName, ns, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Best effort: a failed lookup must not fail the deploy. Falling through to the empty
            // version reproduces the pre-AB#4955 behaviour rather than blocking the reconcile.
            _logger.LogWarning(e,
                "Could not read the installed chart version of release '{Release}' — reconciling with the newest chart instead",
                release);
            return workload.ChartVersion;
        }

        if (string.IsNullOrWhiteSpace(installed))
        {
            _logger.LogInformation(
                "Reconciling unpinned workload '{WorkloadName}' (release '{Release}'): nothing installed to read a version from, resolving the newest chart",
                workload.WorkloadName, release);
            return workload.ChartVersion;
        }

        _logger.LogInformation(
            "Reconciling unpinned workload '{WorkloadName}' (release '{Release}'): keeping the installed chart version {ChartVersion} instead of resolving the newest chart (AB#4955)",
            workload.WorkloadName, release, installed);
        return installed;
    }

    /// <summary>
    /// Clears an orphaned helm <c>pending-*</c> lock before a deploy (AB#4894). Best effort —
    /// any failure is logged and the deploy proceeds (and then fails on the lock exactly as it
    /// would have without this recovery). The lock is only cleared when the pending release
    /// secret is older than <see cref="StaleHelmLockThreshold"/>: this operator runs its deploy
    /// queue serially, so the only legitimate concurrent helm run is on the outgoing pod during
    /// a rolling upgrade — and that one either finishes or dies well within the threshold.
    /// </summary>
    private async Task TryClearStaleHelmLockAsync(string release, string ns, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _helm.GetLatestReleaseRevisionAsync(release, ns, cancellationToken);
            if (latest is not { IsPending: true })
            {
                return;
            }

            var lockSecret = $"sh.helm.release.v1.{release}.v{latest.Revision}";
            var createdAt = await _gateway.GetSecretCreationTimestampAsync(ns, lockSecret, cancellationToken);
            if (createdAt == null)
            {
                return;
            }

            var age = DateTime.UtcNow - createdAt.Value.ToUniversalTime();
            if (age < StaleHelmLockThreshold)
            {
                _logger.LogInformation(
                    "Release '{Release}' revision {Revision} is {Status} but only {AgeMinutes:F1} min old — assuming a live helm run, not touching the lock",
                    release, latest.Revision, latest.Status, age.TotalMinutes);
                return;
            }

            _logger.LogWarning(
                "Release '{Release}' revision {Revision} is stuck in {Status} for {AgeMinutes:F0} min (orphaned helm lock, AB#4894) — deleting release secret '{Secret}' to unblock the deploy",
                release, latest.Revision, latest.Status, age.TotalMinutes, lockSecret);
            await _gateway.DeleteSecretAsync(ns, lockSecret, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Stale-lock check for release '{Release}' failed — proceeding with the deploy", release);
        }
    }

    public async Task UndeployAsync(WorkloadUndeployedDto workload, CancellationToken cancellationToken)
    {
        var release = ReleaseName(workload.TenantId, workload.WorkloadRtId);
        var ns = ResolveNamespace(workload.WorkloadType);
        var secretName = SecretName(release);

        _logger.LogInformation(
            "Undeploying workload: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId}), release '{Release}'",
            workload.TenantId, workload.DeploymentSiteRtId,
            workload.WorkloadName, workload.WorkloadRtId, release);

        // If a deploy is currently in flight for this release, cancel it so
        // helm uninstall doesn't serialize behind helm's atomic timeout (up
        // to 5 min). A short grace window lets the cancelled deploy roll
        // back atomically before we touch the release again — without it
        // we'd race the in-flight Kill against the uninstall.
        if (_inFlightDeploys.TryGetValue(release, out var inFlightCts))
        {
            _logger.LogInformation(
                "Cancelling in-flight deploy for release '{Release}' before undeploy", release);
            try
            {
                inFlightCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The deploy finished and disposed its CTS between TryGetValue
                // and Cancel — that's exactly the outcome we wanted, proceed.
            }

            try
            {
                await Task.Delay(CancelGracePeriod, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Caller dropped the request — drop our work too.
                throw;
            }
        }

        await _helm.UninstallAsync(release, ns, cancellationToken);

        if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
        {
            await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
        }
    }

    public async Task<int> ScaleAsync(ScaleWorkloadDto workload, CancellationToken cancellationToken)
    {
        var release = ReleaseName(workload.TenantId, workload.WorkloadRtId);
        var ns = ResolveNamespace(workload.WorkloadType);

        _logger.LogInformation(
            "Scaling workload: tenant '{TenantId}', workload '{WorkloadName}' (rtId {WorkloadRtId}), release '{Release}' to {Replicas} replica(s)",
            workload.TenantId, workload.WorkloadName, workload.WorkloadRtId, release, workload.Replicas);

        var patched = await _gateway.ScaleDeploymentsByInstanceAsync(ns, release, workload.Replicas,
            cancellationToken);
        if (patched == 0)
        {
            _logger.LogWarning(
                "No Deployments found for release '{Release}' (label app.kubernetes.io/instance) in namespace '{Namespace}'; nothing scaled",
                release, ns);
        }

        return patched;
    }

    private async Task ReconcileSecretAsync(string ns, string secretName, WorkloadDeployedDto workload,
        V1OwnerReference? ownerReference, CancellationToken cancellationToken)
    {
        var secretEntries = workload.Values.Where(v => v.IsSecret).ToArray();

        if (secretEntries.Length == 0)
        {
            if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
            {
                _logger.LogInformation("No secret values for release '{Secret}'; removing stale secret", secretName);
                await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
            }
            return;
        }

        // Always replace: simplest path that handles add / update / remove
        // of individual keys without per-key diffing.
        if (await _gateway.SecretExistsAsync(ns, secretName, cancellationToken))
        {
            await _gateway.DeleteSecretAsync(ns, secretName, cancellationToken);
        }

        var data = new Dictionary<string, byte[]>(secretEntries.Length);
        foreach (var entry in secretEntries)
        {
            data[entry.Path] = Encoding.UTF8.GetBytes(entry.Value ?? string.Empty);
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/tenant"] = SanitizeLabelValue(workload.TenantId),
                    ["octo-mesh.meshmakers.io/deployment-site-rt-id"] = workload.DeploymentSiteRtId,
                    ["octo-mesh.meshmakers.io/workload-rt-id"] = workload.WorkloadRtId,
                    ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator",
                },
                Annotations = new Dictionary<string, string>
                {
                    ["octo-mesh.meshmakers.io/workload-name"] = workload.WorkloadName,
                },
                // Null for every non-adapter-pool workload — those are undeployed through the controller
                // cascade and have never needed a garbage-collection safety net (AB#4924).
                OwnerReferences = ownerReference is null ? null : [ownerReference],
            },
            Type = "Opaque",
            Data = data,
        };

        _logger.LogInformation("Creating secret '{Secret}' in namespace '{Namespace}' with {Count} entries",
            secretName, ns, data.Count);
        await _gateway.CreateSecretAsync(ns, secret, cancellationToken);
    }

    /// <summary>
    /// Returns the workload's existing overrides plus any cluster-credential
    /// overrides the operator can supply, in three tiers. The broker
    /// password (<c>secrets.rabbitmq</c>) is injected unconditionally —
    /// every adapter needs the controller command bus. The root CA
    /// (<c>secrets.rootCa</c>) is likewise injected unconditionally whenever
    /// configured — every workload needs the same TLS trust anchor to reach
    /// the Communication Controller — but unlike every other entry here it
    /// is not secret-flagged, since the workload chart's own
    /// <c>secrets.rootCa</c> template requires a literal string to
    /// <c>b64enc</c> directly (see below). The data-store credentials
    /// (Mongo / CrateDB) are gated on the workload's
    /// <c>ReceivesClusterSecrets</c> opt-in and marked
    /// <c>IsSecret = true</c> so they flow through the per-release
    /// Kubernetes Secret rather than appearing as a plain value in the
    /// rendered manifest. Entries the operator does not have a value for
    /// are skipped silently.
    /// </summary>
    internal static IReadOnlyList<ValueOverrideDto> AppendClusterSecrets(
        IReadOnlyList<ValueOverrideDto> existing, bool receivesClusterSecrets,
        WorkloadTypeDto workloadType, OperatorOptions options)
    {
        var injected = new List<ValueOverrideDto>(5);

        // The RabbitMQ broker password is part of the basic controller↔adapter
        // contract — every adapter needs the command bus, regardless of whether
        // it also touches data stores. Inject it whenever the operator has a
        // BrokerPassword, independent of the ReceivesClusterSecrets opt-in.
        // Previously this was lumped into the cluster-secrets gate, which made
        // pure edge adapters (e.g. Modbus / Loxone) fail the chart's
        // `secrets.rabbitmq must be set` validation unless the user enabled a
        // flag whose name implies cluster-integration semantics it doesn't
        // actually need.
        if (!string.IsNullOrEmpty(options.BrokerPassword))
        {
            injected.Add(new ValueOverrideDto { Path = "secrets.rabbitmq", Value = options.BrokerPassword, IsSecret = true });
        }

        // The root CA the operator itself was given trusts the same
        // private-CA cluster the workload's TLS connection to the
        // Communication Controller needs to validate. Same unconditional
        // gate as BrokerPassword — a workload with ReceivesClusterSecrets
        // false (e.g. the simulation adapter) still talks TLS to the
        // controller and would otherwise fail the handshake and never
        // register. Not secret-flagged: the workload chart's own
        // `secrets.rootCa` template (its `{fullname}-ca` Secret) `b64enc`s
        // `.Values.secrets.rootCa` directly and requires a plain string —
        // a `valueFrom.secretKeyRef` map there would break chart rendering.
        if (!string.IsNullOrEmpty(options.RootCaCertificate))
        {
            injected.Add(new ValueOverrideDto { Path = "secrets.rootCa", Value = options.RootCaCertificate, IsSecret = false });
        }

        // 🔴 AB#4924: an adapter POOL never receives them, whatever the entity says. These are the
        // cluster's SHARED data-store credentials — one Mongo user, one CrateDB user, every
        // tenant's data behind them. A pool member executes work for tenants other than the one
        // that owns it, and a lease is the mechanism that hands it exactly one tenant at a time;
        // a standing credential to all of them makes that mechanism decorative. What a member does
        // get is the two unconditional tiers above (the RabbitMQ command bus and the TLS trust
        // anchor, neither of which carries tenant authority) plus its own per-release secret in
        // the platform namespace. The controller refuses to set ReceivesClusterSecrets on a deployment site as
        // well — two gates, one on each side of the wire, because either side alone is one edit
        // away from silence.
        //
        // 🔴 WHERE TENANT-SCOPED DATA ACCESS COMES FROM INSTEAD, precisely — this comment used to
        // assert it and the code did not yet provide it:
        //
        //   MONGO: the lease carries the borrowing tenant's database name, database user and
        //   database password (LeaseDto.DatabaseName/DatabaseUser/DatabasePassword, resolved by
        //   the controller's TenantDatabaseCredentialResolver). The member installs them for that
        //   one database through ITenantDatabaseCredentialSource and drops them — and the engine's
        //   cached, authenticated repository clients — on release. A lease whose credential cannot
        //   be resolved is refused, and a member handed a lease without one refuses to take it, so
        //   neither side can degrade into "use whatever this process happens to hold". The password
        //   itself is still installation-wide today; AB#5255 makes it per database and changes only
        //   that resolver.
        //
        //   CRATEDB: NOT covered, and deliberately. There is no per-tenant CrateDB principal to
        //   carry — one connection string per installation, tenants separated by schema — so a
        //   lease-carried stream-data credential would be time-scoped but not tenant-scoped, which
        //   is the shape of the Mongo mechanism without its substance. Withholding
        //   secrets.streamDataPassword therefore means a leased pipeline that writes an archive
        //   fails to connect rather than reaching another tenant's schema. Archive-writing
        //   pipelines stay on dedicated adapters until a per-tenant CrateDB user exists (the
        //   stream-data sibling of AB#5255).
        if (workloadType == WorkloadTypeDto.AdapterPool)
        {
            receivesClusterSecrets = false;
        }

        // Data-store credentials (Mongo / CrateDB) only matter for adapters
        // that actually open those connections. Gate on the explicit opt-in
        // so a Mongo-less Modbus pod doesn't carry Mongo creds in its Secret.
        if (receivesClusterSecrets)
        {
            if (!string.IsNullOrEmpty(options.ClusterSecrets.MongodbUserPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.databaseUser", Value = options.ClusterSecrets.MongodbUserPassword, IsSecret = true });
            }
            if (!string.IsNullOrEmpty(options.ClusterSecrets.MongodbAdminPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.databaseAdmin", Value = options.ClusterSecrets.MongodbAdminPassword, IsSecret = true });
            }
            if (!string.IsNullOrEmpty(options.ClusterSecrets.StreamDataPassword))
            {
                injected.Add(new ValueOverrideDto { Path = "secrets.streamDataPassword", Value = options.ClusterSecrets.StreamDataPassword, IsSecret = true });
            }
        }

        if (injected.Count == 0)
        {
            return existing;
        }

        // Workload-supplied overrides win — operator-injected entries are
        // appended first so the same path coming from the entity overrides
        // the operator's value. WorkloadOverrideYamlBuilder.SetNested keeps
        // only the last value per path.
        var merged = new List<ValueOverrideDto>(existing.Count + injected.Count);
        merged.AddRange(injected);
        merged.AddRange(existing);
        return merged;
    }

    /// <summary>
    /// Kubernetes namespace a workload is deployed into (AB#4924).
    ///
    /// Everything except an adapter pool goes to <see cref="OperatorOptions.DeploymentSiteNamespace"/>,
    /// unchanged. A deployment site goes to <see cref="OperatorOptions.PlatformNamespace"/> when one is
    /// configured, because its members run work for tenants other than the one that owns it and
    /// must not sit among that tenant's own workloads (concept §4b, Q1). An unset platform
    /// namespace resolves to the deployment site namespace — see the option's documentation for why that is
    /// the correct default and not a fallback.
    /// </summary>
    internal string ResolveNamespace(WorkloadTypeDto workloadType) =>
        workloadType == WorkloadTypeDto.AdapterPool && !string.IsNullOrWhiteSpace(_options.PlatformNamespace)
            ? _options.PlatformNamespace!
            : _options.DeploymentSiteNamespace;

    /// <summary>
    /// Owner reference to the lending tenant's <c>DeploymentSite</c> CR for a deployment site workload,
    /// or <c>null</c> for anything else — and for a deployment site whose namespace is not the CR's
    /// namespace (AB#4924).
    ///
    /// 🔴 That last case is a refusal, not a gap. Kubernetes forbids cross-namespace owner
    /// references: a namespaced dependent whose owner lives elsewhere is treated as having a
    /// <i>missing</i> owner and is deleted by the garbage collector. Writing one anyway would not
    /// merely fail to clean up after a deleted tenant, it would delete a live tenant's deployment site within
    /// seconds of the deploy. So the reference is written only where it is valid, and its absence
    /// is logged where it is not.
    /// </summary>
    private async Task<V1OwnerReference?> TryResolveDeploymentSiteOwnerReferenceAsync(WorkloadDeployedDto workload,
        string ns, CancellationToken cancellationToken)
    {
        if (workload.WorkloadType != WorkloadTypeDto.AdapterPool)
        {
            return null;
        }

        if (!string.Equals(ns, _options.DeploymentSiteNamespace, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Adapter pool '{WorkloadName}' is deployed to namespace '{Namespace}' but its tenant's DeploymentSite CR lives in '{CrNamespace}'. Kubernetes rejects cross-namespace owner references, so the pool will NOT be garbage-collected when tenant '{TenantId}' is deleted; it is removed by the controller's undeploy cascade only.",
                workload.WorkloadName, ns, _options.DeploymentSiteNamespace, workload.TenantId);
            return null;
        }

        try
        {
            var crName = DeploymentSiteManager.GetCrName(workload.TenantId, workload.DeploymentSiteRtId);
            var ownerReference =
                await _gateway.TryGetDeploymentSiteOwnerReferenceAsync(ns, crName, cancellationToken);
            if (ownerReference == null)
            {
                _logger.LogWarning(
                    "No DeploymentSite CR '{CrName}' in namespace '{Namespace}' to own adapter pool '{WorkloadName}'; deploying without an owner reference",
                    crName, ns, workload.WorkloadName);
            }

            return ownerReference;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Best effort, like every other cluster-state read on this path: garbage collection is
            // a safety net behind the controller's undeploy cascade, and losing the net is not a
            // reason to refuse the deploy.
            _logger.LogWarning(e,
                "Could not resolve the owner reference for adapter pool '{WorkloadName}'; deploying without one",
                workload.WorkloadName);
            return null;
        }
    }

    /// <summary>
    /// Stamps the resolved owner reference onto the Deployments helm just created (AB#4924).
    /// No-op when there is no reference to write. Best effort: the release is already installed
    /// and running at this point, and failing the deploy over a missing safety net would trade a
    /// working deployment site for a clean one.
    /// </summary>
    private async Task ApplyDeploymentSiteOwnerReferenceAsync(string release, string ns, V1OwnerReference? ownerReference,
        CancellationToken cancellationToken)
    {
        if (ownerReference == null)
        {
            return;
        }

        try
        {
            var patched = await _gateway.SetDeploymentOwnerReferenceByInstanceAsync(ns, release, ownerReference,
                cancellationToken);
            if (patched == 0)
            {
                _logger.LogWarning(
                    "Release '{Release}' has no Deployments to own in namespace '{Namespace}'; the adapter pool will not be garbage-collected with its tenant",
                    release, ns);
                return;
            }

            _logger.LogInformation(
                "Adapter pool release '{Release}': {Count} Deployment(s) now owned by DeploymentSite '{Owner}'",
                release, patched, ownerReference.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Could not write the owner reference onto release '{Release}'; the adapter pool will not be garbage-collected with its tenant",
                release);
        }
    }

    /// <summary>
    /// Helm release name <c>{tenantId}-{workloadRtId}</c>. The workload's
    /// runtime entity id is a 24-char lowercase hex string and always
    /// RFC 1123 valid, so renaming the user-facing WorkloadName in the
    /// Studio does not orphan the helm release. Delegates to
    /// <see cref="K8sNaming.DnsName(int,string[])"/> so the reconciler
    /// and the DeploymentSiteManager produce identical names for
    /// matching CK identifiers.
    /// </summary>
    internal static string ReleaseName(string tenantId, string workloadRtId) =>
        K8sNaming.DnsName(K8sNaming.DefaultDnsNameMaxLength, tenantId, workloadRtId);

    internal static string SecretName(string release) => $"{release}-octo-secrets";

    /// <summary>
    /// Coerces an arbitrary string into a valid Kubernetes label value.
    /// Thin wrapper around <see cref="K8sNaming.LabelValue"/> retained so
    /// existing call sites in this file and the test assembly continue to
    /// work; new code should call <see cref="K8sNaming.LabelValue"/>
    /// directly.
    /// </summary>
    internal static string SanitizeLabelValue(string value) => K8sNaming.LabelValue(value);

    /// <summary>
    /// Stable, DNS-safe alias derived from the repository URL. Same URL
    /// produces the same alias every time so repeated <c>helm repo add</c>
    /// calls are idempotent.
    /// </summary>
    internal static string RepoAlias(string repositoryUrl)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(repositoryUrl));
        var hex = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        return $"octo-{hex}";
    }
}
