using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Background service that maintains a SignalR management connection to the Communication Controller.
/// Connects whenever <c>CommunicationControllerUri</c> is configured — required in both
/// central and edge modes so that deployment site register/unregister, workload deploys, and tenant
/// lifecycle events flow through the hub. The <c>AutoManageDeploymentSites</c> flag only gates the
/// secondary behavior of auto-creating/auto-deleting <c>DeploymentSite</c> CRs in
/// response to <c>DeploymentSiteDeployedAsync</c> / <c>DeploymentSiteUndeployedAsync</c> events — used by the
/// central operator, not by edge operators (which receive CRs manually).
/// </summary>
public class OperatorHubService : BackgroundService, IOperatorHubCallbacks, IOperatorHubInvoker
{
    private readonly ILogger<OperatorHubService> _logger;
    private readonly OperatorOptions _options;
    private readonly IOperatorHubClientFactory _clientFactory;
    private readonly IDeploymentSiteManager _deploymentSiteManager;
    private readonly IWorkloadReconciler _workloadReconciler;
    private readonly IServiceProvider _serviceProvider;

    // Set in ExecuteAsync once the client is constructed; consumed by the
    // workload-deploy callback to report success / failure back to the
    // controller, and by IOperatorHubInvoker for deployment site register / unregister.
    // Stays null when no CommunicationControllerUri is configured (the
    // service returns early without building a client).
    private IOperatorHubClient? _client;

    // Latches to 1 the first time a controller rejects
    // ReportWorkloadDeploymentProgressAsync with HubException (older
    // controller build that does not implement the method). Subsequent calls
    // are still attempted — they will keep throwing — but the warning log
    // fires only once so the watcher's 3-second pulse does not flood the log.
    private int _progressUnsupportedLogged;

    // Same once-only latch for ReportWorkloadScaleStatusAsync (AB#4917) —
    // rejected by controller builds that pre-date the on-demand lifecycle.
    private int _scaleStatusUnsupportedLogged;

    public OperatorHubService(
        ILogger<OperatorHubService> logger,
        IOptions<OperatorOptions> options,
        IOperatorHubClientFactory clientFactory,
        IDeploymentSiteManager deploymentSiteManager,
        IWorkloadReconciler workloadReconciler,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _clientFactory = clientFactory;
        _deploymentSiteManager = deploymentSiteManager;
        _workloadReconciler = workloadReconciler;
        // IDeploymentSiteService is resolved lazily to break the DI cycle: DeploymentSiteService
        // depends on IOperatorHubInvoker (this class), and we depend on
        // IDeploymentSiteService here. Both are singletons; lazy resolution defers the
        // lookup until ExecuteAsync runs.
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.IsAlive ?? false;

    /// <inheritdoc />
    public async Task RegisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping RegisterDeploymentSiteAsync for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId} (will be replayed on reconnect)",
                tenantId, deploymentSiteRtId);
            return;
        }
        await client.RegisterDeploymentSiteAsync(tenantId, deploymentSiteRtId);
    }

    /// <inheritdoc />
    public async Task UnregisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping UnregisterDeploymentSiteAsync for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
            return;
        }
        await client.UnregisterDeploymentSiteAsync(tenantId, deploymentSiteRtId);
    }

    /// <inheritdoc />
    public async Task ReportDeployedDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        // Edge operators must NOT call ReportDeployedStateAsync — the
        // controller-side handler rejects them with a HubException. Skip at
        // the source so every CR reconcile on an edge operator doesn't emit
        // an avoidable error audit event on the controller.
        if (!_options.AutoManageDeploymentSites)
        {
            return;
        }
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            // Same contract as RegisterDeploymentSiteAsync: when the hub is down we
            // skip. The next bulk reverse-sync (fired from the reconnect
            // callback once the connection is restored) covers the gap as
            // long as the deployment site is in DeploymentSiteService.GetDeploymentSites() at that point.
            _logger.LogDebug(
                "Operator-hub not connected; skipping per-deployment-site reverse-sync for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
            return;
        }
        try
        {
            await client.ReportDeployedStateAsync(new[]
            {
                new OperatorDeployedDeploymentSiteReportDto
                {
                    TenantId = tenantId,
                    DeploymentSiteRtId = deploymentSiteRtId,
                    DeploymentSiteName = string.Empty,
                    WorkloadRtIds = Array.Empty<string>(),
                },
            });
        }
        catch (Exception ex)
        {
            // Best-effort, same rationale as the bulk reverse-sync: a missing
            // / older controller-side contract must not break the per-CR
            // reconcile loop. Log so the drift is at least diagnosable.
            _logger.LogWarning(ex,
                "Failed to send per-deployment-site reverse-sync for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
        }
    }

    /// <inheritdoc />
    public async Task ReportWorkloadDeploymentProgressAsync(WorkloadDeploymentProgressDto progress)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            return;
        }

        try
        {
            await client.ReportWorkloadDeploymentProgressAsync(progress);
        }
        catch (HubException ex)
        {
            // Older controller builds reject the call with "Method does not
            // exist on the server". Log once and keep degrading silently —
            // the watcher fires every few seconds and we don't want every
            // tick to dump a stack trace.
            if (Interlocked.CompareExchange(ref _progressUnsupportedLogged, 1, 0) == 0)
            {
                _logger.LogWarning(ex,
                    "Controller does not accept ReportWorkloadDeploymentProgressAsync — falling back to terminal status reports. Upgrade the controller to enable live deploy feedback.");
            }
        }
        catch (Exception ex)
        {
            // Connection drop mid-call, serializer mismatch on an in-flight
            // upgrade, etc. — log at debug because the next tick will retry
            // and the terminal status report still goes through.
            _logger.LogDebug(ex,
                "ReportWorkloadDeploymentProgressAsync failed for tenant '{TenantId}', workload '{WorkloadName}'",
                progress.TenantId, progress.WorkloadName);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CommunicationControllerUri))
        {
            _logger.LogWarning(
                "CommunicationControllerUri is not configured, operator hub service will not start " +
                "(deployment site register/unregister and workload-deploy events will be unavailable)");
            return;
        }

        _logger.LogInformation(
            "Starting operator hub service in {Mode} mode, connecting to controller at {ControllerUri}",
            _options.AutoManageDeploymentSites ? "central (AutoManageDeploymentSites=true)" : "edge (AutoManageDeploymentSites=false)",
            _options.CommunicationControllerUri);

        var clientOptions = new OperatorHubClientOptions
        {
            EndpointUri = _options.CommunicationControllerUri
        };

        var client = _clientFactory.Create(clientOptions, this);
        _client = client;

        var onReconnect = async (bool isReconnect) =>
        {
            _logger.LogInformation("Registering operator with controller (reconnect: {IsReconnect})", isReconnect);

            // Flip every owned deployment site to unregistered before replaying the
            // registrations below. A deployment site that registered fine on a PREVIOUS
            // connection but fails now would otherwise keep a stale
            // IsRegistered=true and be invisible to the periodic retry loop.
            var deploymentSiteService = _serviceProvider.GetRequiredService<IDeploymentSiteService>();
            deploymentSiteService.ResetRegistrationState();

            // Declare our mode to the controller so it can validate that we
            // only register deployment sites whose Environment matches it (central op
            // → Cloud, edge op → Edge). Without this declaration the
            // controller treats us as legacy and skips enforcement.
            var deployedDeploymentSites = (await client.RegisterOperatorAsync(_options.AutoManageDeploymentSites)).ToArray();
            _logger.LogInformation("Registered with controller, {DeploymentSiteCount} deployed Cloud deployment sites",
                deployedDeploymentSites.Length);

            // Same gate as DeploymentSiteDeployedAsync: auto-CR-creation is the central
            // operator's job. Without this check an edge operator would
            // materialize CRs (and broker secrets) for every Cloud deployment site the
            // controller knows about on every (re)connect, then register them
            // as if it owned them — workload events would then route to the
            // edge cluster too.
            if (_options.AutoManageDeploymentSites)
            {
                foreach (var deploymentSite in deployedDeploymentSites)
                {
                    await _deploymentSiteManager.CreateDeploymentSiteAsync(deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
                }
            }
            else if (deployedDeploymentSites.Length > 0)
            {
                _logger.LogDebug(
                    "AutoManageDeploymentSites=false: skipping CR creation for {DeploymentSiteCount} deployed Cloud deployment sites returned by RegisterOperatorAsync",
                    deployedDeploymentSites.Length);
            }

            // Replay deployment site registrations for every DeploymentSite CR the
            // operator currently owns. On a fresh connect this is empty (CRs
            // arrive via DeploymentSiteDeployedAsync afterwards), on a reconnect this
            // is what flips every deployment site back to Online.
            var ownedDeploymentSites = deploymentSiteService.GetDeploymentSites().ToArray();
            foreach (var deploymentSite in ownedDeploymentSites)
            {
                try
                {
                    await client.RegisterDeploymentSiteAsync(deploymentSite.Entity.Spec.TenantId,
                        deploymentSite.Entity.Spec.DeploymentSiteRtId);
                    deploymentSite.IsRegistered = true;
                }
                catch (Exception ex)
                {
                    // Leave IsRegistered=false (reset above) so the periodic
                    // retry loop picks the deployment site up — the controller may have
                    // rejected the call transiently (e.g. CkCache still
                    // warming up during a parallel service startup, AB#4371).
                    _logger.LogWarning(ex,
                        "Failed to re-register deployment site rtId {DeploymentSiteRtId} for tenant '{TenantId}' on reconnect; " +
                        "the periodic registration retry will pick it up",
                        deploymentSite.Entity.Spec.DeploymentSiteRtId, deploymentSite.Entity.Spec.TenantId);
                }
            }

            // Reverse-sync: tell the controller which deployment sites we currently have
            // an active CR for so it can lift any DeploymentState that drifted
            // back to Pending (e.g. operator pod was restarted while the
            // controller stayed up, the CR survived in k8s but the controller's
            // in-memory tracking was lost). Cloud-only by hub contract — edge
            // operators would be rejected with a HubException. Workload-level
            // reverse-sync is NOT yet covered: the operator has no persistent
            // helm-release-to-workload-rtId mapping, so we report each deployment site
            // with an empty WorkloadRtIds list and rely on the controller's
            // own tracking + the existing DeploymentSiteDeployedAsync fan-out for
            // workloads. Documented as a follow-up in CLAUDE.md.
            if (_options.AutoManageDeploymentSites && ownedDeploymentSites.Length > 0)
            {
                var reports = ownedDeploymentSites
                    .Select(p => new OperatorDeployedDeploymentSiteReportDto
                    {
                        TenantId = p.Entity.Spec.TenantId,
                        DeploymentSiteRtId = p.Entity.Spec.DeploymentSiteRtId,
                        // CR doesn't carry the human-readable name (it lives on
                        // the controller's RtDeploymentSite.Name); the controller-side
                        // restore loads the name itself for log messages, so an
                        // empty value here is fine.
                        DeploymentSiteName = string.Empty,
                        WorkloadRtIds = Array.Empty<string>(),
                    })
                    .ToArray();

                try
                {
                    await client.ReportDeployedStateAsync(reports);
                    _logger.LogInformation(
                        "Reverse-sync sent to controller: {Count} deployment site(s)",
                        reports.Length);
                }
                catch (Exception ex)
                {
                    // Self-healing is best-effort. Failing the report does not
                    // break ongoing operations — the next deploy / undeploy
                    // event will write the state correctly anyway. Log so the
                    // partial drift is at least diagnosable.
                    _logger.LogWarning(ex,
                        "Failed to send reverse-sync to controller ({Count} deployment site(s) skipped)",
                        reports.Length);
                }
            }
        };

        var registrationRetryTask = RetryDeploymentSiteRegistrationLoopAsync(client, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.StartAsync(onReconnect, stoppingToken);
                client.EnableReconnect(onReconnect);

                _logger.LogInformation("Operator hub connected, waiting for deployment site events");

                // Keep running until cancelled
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Operator hub connection failed, retrying in 30 seconds");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        await registrationRetryTask;

        try
        {
            await client.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping operator hub client");
        }
    }

    /// <summary>
    /// Periodic self-heal for deployment site registrations that failed while the hub
    /// connection stayed alive. The reconnect callback re-registers every
    /// owned deployment site, but a registration the CONTROLLER rejects (e.g. a
    /// transient CkCache error while its tenant models are still importing
    /// during a parallel service startup) used to be logged and forgotten:
    /// the connection never drops, so no reconnect fires, and the deployment site
    /// stays orphaned — the controller then drops every workload
    /// deploy/undeploy for it ("No operator currently owns deployment site ...").
    /// Observed on prod-1, AB#4371. This loop retries every owned deployment site
    /// that is not flagged <c>IsRegistered</c> while the connection is
    /// alive; a recovered deployment site also gets the per-deployment-site reverse-sync so a
    /// drifted <c>DeploymentState</c> is restored.
    /// </summary>
    private async Task RetryDeploymentSiteRegistrationLoopAsync(IOperatorHubClient client, CancellationToken stoppingToken)
    {
        if (_options.DeploymentSiteRegistrationRetrySeconds <= 0)
        {
            _logger.LogWarning(
                "DeploymentSiteRegistrationRetrySeconds is {RetrySeconds}; deployment site-registration retry is disabled — " +
                "a registration rejected by the controller will not be re-attempted until the next reconnect",
                _options.DeploymentSiteRegistrationRetrySeconds);
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.DeploymentSiteRegistrationRetrySeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!client.IsAlive)
            {
                // Connection is down: registration would no-op anyway and the
                // reconnect callback re-registers everything once it's back.
                continue;
            }

            var deploymentSiteService = _serviceProvider.GetRequiredService<IDeploymentSiteService>();
            foreach (var deploymentSite in deploymentSiteService.GetDeploymentSites().Where(p => !p.IsRegistered))
            {
                var tenantId = deploymentSite.Entity.Spec.TenantId;
                var deploymentSiteRtId = deploymentSite.Entity.Spec.DeploymentSiteRtId;
                try
                {
                    await client.RegisterDeploymentSiteAsync(tenantId, deploymentSiteRtId);
                    deploymentSite.IsRegistered = true;
                    _logger.LogInformation(
                        "Recovered registration for deployment site rtId {DeploymentSiteRtId} (tenant '{TenantId}') after an earlier failure",
                        deploymentSiteRtId, tenantId);
                    // Same follow-up as DeploymentSiteService.RegisterDeploymentSiteAsync: restore a
                    // DeploymentState that drifted while the deployment site was orphaned.
                    // Gated internally to Cloud mode; best-effort by contract.
                    await ReportDeployedDeploymentSiteAsync(tenantId, deploymentSiteRtId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Registration retry failed for deployment site rtId {DeploymentSiteRtId} (tenant '{TenantId}'); " +
                        "next attempt in {RetrySeconds}s",
                        deploymentSiteRtId, tenantId, _options.DeploymentSiteRegistrationRetrySeconds);
                }
            }
        }
    }

    public async Task DeploymentSiteDeployedAsync(DeployedDeploymentSiteDto deploymentSite)
    {
        _logger.LogInformation(
            "DeploymentSite deployed event received: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);

        // Auto-CR-creation is the central-operator's job. Edge operators
        // receive the same broadcast (the controller fans out to every
        // connected operator) but must ignore it — CRs there are managed
        // out-of-band (manually or by an external system).
        if (!_options.AutoManageDeploymentSites)
        {
            _logger.LogDebug(
                "AutoManageDeploymentSites=false: not auto-creating CR for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
            return;
        }

        try
        {
            await _deploymentSiteManager.CreateDeploymentSiteAsync(deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create DeploymentSite CR for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
        }
    }

    public async Task DeploymentSiteUndeployedAsync(string tenantId, string deploymentSiteRtId)
    {
        _logger.LogInformation(
            "DeploymentSite undeployed event received: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            tenantId, deploymentSiteRtId);

        if (!_options.AutoManageDeploymentSites)
        {
            _logger.LogDebug(
                "AutoManageDeploymentSites=false: not auto-deleting CR for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
            return;
        }

        try
        {
            await _deploymentSiteManager.DeleteDeploymentSiteAsync(tenantId, deploymentSiteRtId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete DeploymentSite CR for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
        }
    }

    public async Task WorkloadDeployedAsync(WorkloadDeployedDto workload)
    {
        _logger.LogInformation(
            "Workload deployed event received: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}', type '{WorkloadType}', chart '{ChartName}:{ChartVersion}'",
            workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName,
            workload.WorkloadType, workload.ChartName, workload.ChartVersion);

        bool success;
        string? statusMessage;
        try
        {
            await _workloadReconciler.DeployAsync(workload, CancellationToken.None);
            success = true;
            statusMessage = null;
        }
        catch (Exception ex)
        {
            // Don't let a single bad workload crash the hub connection.
            _logger.LogError(ex,
                "Failed to deploy workload '{WorkloadName}' for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                workload.WorkloadName, workload.TenantId, workload.DeploymentSiteRtId);
            success = false;
            statusMessage = ex.Message;
        }

        // Report the outcome back to the controller so the workload's
        // DeploymentState / StatusMessage on the runtime entity reflect what
        // actually happened in the cluster. Wrap the report in its own
        // try/catch — if the round-trip itself fails (e.g. the connection
        // dropped), the operator log already has the deploy outcome and the
        // next deploy attempt will set the state.
        try
        {
            await ReportDeploymentStatusAsync(workload, success, statusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to report deployment status for workload '{WorkloadName}' (tenant '{TenantId}')",
                workload.WorkloadName, workload.TenantId);
        }
    }

    private async Task ReportDeploymentStatusAsync(WorkloadDeployedDto workload, bool success, string? statusMessage)
    {
        var client = _client;
        if (client == null)
        {
            // No active hub connection (service never started or already
            // stopped); nothing to report to.
            return;
        }

        await client.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = workload.TenantId,
            WorkloadName = workload.WorkloadName,
            WorkloadRtId = workload.WorkloadRtId,
            Success = success,
            StatusMessage = statusMessage,
        });
    }

    public async Task ScaleWorkloadAsync(ScaleWorkloadDto workload)
    {
        _logger.LogInformation(
            "Workload scale event received: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}', replicas {Replicas}",
            workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName, workload.Replicas);

        bool success;
        string? statusMessage;
        try
        {
            var patched = await _workloadReconciler.ScaleAsync(workload, CancellationToken.None);
            success = patched > 0;
            statusMessage = success
                ? $"Scaled {patched} deployment(s) to {workload.Replicas} replica(s)."
                : "No Deployments found for the workload's helm release; nothing scaled.";
        }
        catch (Exception ex)
        {
            // Same rule as deploy/undeploy: one bad workload must not crash
            // the hub connection.
            _logger.LogError(ex,
                "Failed to scale workload '{WorkloadName}' for tenant '{TenantId}' to {Replicas} replica(s)",
                workload.WorkloadName, workload.TenantId, workload.Replicas);
            success = false;
            statusMessage = ex.Message;
        }

        try
        {
            await ReportScaleStatusAsync(workload, success, statusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to report scale status for workload '{WorkloadName}' (tenant '{TenantId}')",
                workload.WorkloadName, workload.TenantId);
        }
    }

    private async Task ReportScaleStatusAsync(ScaleWorkloadDto workload, bool success, string? statusMessage)
    {
        var client = _client;
        if (client == null)
        {
            return;
        }

        try
        {
            await client.ReportWorkloadScaleStatusAsync(new WorkloadScaleStatusDto
            {
                TenantId = workload.TenantId,
                WorkloadRtId = workload.WorkloadRtId,
                WorkloadName = workload.WorkloadName,
                Replicas = workload.Replicas,
                Success = success,
                StatusMessage = statusMessage,
            });
        }
        catch (HubException ex)
        {
            // Older controller builds reject the method — log once, degrade
            // silently (same pattern as the deploy-progress channel). In
            // practice controller and operator ship together, so this only
            // covers a skewed rolling upgrade window.
            if (Interlocked.CompareExchange(ref _scaleStatusUnsupportedLogged, 1, 0) == 0)
            {
                _logger.LogWarning(ex,
                    "Controller does not accept ReportWorkloadScaleStatusAsync — scale acks are dropped. Upgrade the controller to enable the on-demand lifecycle state machine.");
            }
        }
    }

    public async Task WorkloadUndeployedAsync(WorkloadUndeployedDto workload)
    {
        _logger.LogInformation(
            "Workload undeployed event received: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}', type '{WorkloadType}'",
            workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName, workload.WorkloadType);
        try
        {
            await _workloadReconciler.UndeployAsync(workload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to undeploy workload '{WorkloadName}' for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                workload.WorkloadName, workload.TenantId, workload.DeploymentSiteRtId);
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        _logger.LogInformation("Pre-update tenant event received: tenant '{TenantId}'", tenantId);
        try
        {
            var deploymentSiteService = _serviceProvider.GetRequiredService<IDeploymentSiteService>();
            if (deploymentSiteService is IOperatorHubCallbacks_PreUpdateTenantHandler handler)
            {
                await handler.PreUpdateTenantAsync(tenantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to handle pre-update event for tenant '{TenantId}'", tenantId);
        }
    }
}
