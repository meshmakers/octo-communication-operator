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
/// central and edge modes so that pool register/unregister, workload deploys, and tenant
/// lifecycle events flow through the hub. The <c>AutoManagePools</c> flag only gates the
/// secondary behavior of auto-creating/auto-deleting <c>CommunicationPool</c> CRs in
/// response to <c>PoolDeployedAsync</c> / <c>PoolUndeployedAsync</c> events — used by the
/// central operator, not by edge operators (which receive CRs manually).
/// </summary>
public class OperatorHubService : BackgroundService, IOperatorHubCallbacks, IOperatorHubInvoker
{
    private readonly ILogger<OperatorHubService> _logger;
    private readonly OperatorOptions _options;
    private readonly IOperatorHubClientFactory _clientFactory;
    private readonly ICommunicationPoolManager _poolManager;
    private readonly IWorkloadReconciler _workloadReconciler;
    private readonly IServiceProvider _serviceProvider;

    // Set in ExecuteAsync once the client is constructed; consumed by the
    // workload-deploy callback to report success / failure back to the
    // controller, and by IOperatorHubInvoker for pool register / unregister.
    // Stays null when no CommunicationControllerUri is configured (the
    // service returns early without building a client).
    private IOperatorHubClient? _client;

    // Latches to 1 the first time a controller rejects
    // ReportWorkloadDeploymentProgressAsync with HubException (older
    // controller build that does not implement the method). Subsequent calls
    // are still attempted — they will keep throwing — but the warning log
    // fires only once so the watcher's 3-second pulse does not flood the log.
    private int _progressUnsupportedLogged;

    public OperatorHubService(
        ILogger<OperatorHubService> logger,
        IOptions<OperatorOptions> options,
        IOperatorHubClientFactory clientFactory,
        ICommunicationPoolManager poolManager,
        IWorkloadReconciler workloadReconciler,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _clientFactory = clientFactory;
        _poolManager = poolManager;
        _workloadReconciler = workloadReconciler;
        // IPoolService is resolved lazily to break the DI cycle: PoolService
        // depends on IOperatorHubInvoker (this class), and we depend on
        // IPoolService here. Both are singletons; lazy resolution defers the
        // lookup until ExecuteAsync runs.
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.IsAlive ?? false;

    /// <inheritdoc />
    public async Task RegisterPoolAsync(string tenantId, string poolRtId)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping RegisterPoolAsync for tenant '{TenantId}', pool rtId {PoolRtId} (will be replayed on reconnect)",
                tenantId, poolRtId);
            return;
        }
        await client.RegisterPoolAsync(tenantId, poolRtId);
    }

    /// <inheritdoc />
    public async Task UnregisterPoolAsync(string tenantId, string poolRtId)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping UnregisterPoolAsync for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
            return;
        }
        await client.UnregisterPoolAsync(tenantId, poolRtId);
    }

    /// <inheritdoc />
    public async Task ReportDeployedPoolAsync(string tenantId, string poolRtId)
    {
        // Edge operators must NOT call ReportDeployedStateAsync — the
        // controller-side handler rejects them with a HubException. Skip at
        // the source so every CR reconcile on an edge operator doesn't emit
        // an avoidable error audit event on the controller.
        if (!_options.AutoManagePools)
        {
            return;
        }
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            // Same contract as RegisterPoolAsync: when the hub is down we
            // skip. The next bulk reverse-sync (fired from the reconnect
            // callback once the connection is restored) covers the gap as
            // long as the pool is in PoolService.GetPools() at that point.
            _logger.LogDebug(
                "Operator-hub not connected; skipping per-pool reverse-sync for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
            return;
        }
        try
        {
            await client.ReportDeployedStateAsync(new[]
            {
                new OperatorDeployedPoolReportDto
                {
                    TenantId = tenantId,
                    PoolRtId = poolRtId,
                    PoolName = string.Empty,
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
                "Failed to send per-pool reverse-sync for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
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
                "(pool register/unregister and workload-deploy events will be unavailable)");
            return;
        }

        _logger.LogInformation(
            "Starting operator hub service in {Mode} mode, connecting to controller at {ControllerUri}",
            _options.AutoManagePools ? "central (AutoManagePools=true)" : "edge (AutoManagePools=false)",
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
            // Declare our mode to the controller so it can validate that we
            // only register pools whose Environment matches it (central op
            // → Cloud, edge op → Edge). Without this declaration the
            // controller treats us as legacy and skips enforcement.
            var deployedPools = (await client.RegisterOperatorAsync(_options.AutoManagePools)).ToArray();
            _logger.LogInformation("Registered with controller, {PoolCount} deployed Cloud pools",
                deployedPools.Length);

            // Same gate as PoolDeployedAsync: auto-CR-creation is the central
            // operator's job. Without this check an edge operator would
            // materialize CRs (and broker secrets) for every Cloud pool the
            // controller knows about on every (re)connect, then register them
            // as if it owned them — workload events would then route to the
            // edge cluster too.
            if (_options.AutoManagePools)
            {
                foreach (var pool in deployedPools)
                {
                    await _poolManager.CreatePoolAsync(pool.TenantId, pool.PoolRtId);
                }
            }
            else if (deployedPools.Length > 0)
            {
                _logger.LogDebug(
                    "AutoManagePools=false: skipping CR creation for {PoolCount} deployed Cloud pools returned by RegisterOperatorAsync",
                    deployedPools.Length);
            }

            // Replay pool registrations for every CommunicationPool CR the
            // operator currently owns. On a fresh connect this is empty (CRs
            // arrive via PoolDeployedAsync afterwards), on a reconnect this
            // is what flips every pool back to Online.
            var poolService = _serviceProvider.GetRequiredService<IPoolService>();
            var ownedPools = poolService.GetPools().ToArray();
            foreach (var pool in ownedPools)
            {
                try
                {
                    await client.RegisterPoolAsync(pool.Entity.Spec.TenantId,
                        pool.Entity.Spec.PoolRtId);
                    pool.IsRegistered = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to re-register pool rtId {PoolRtId} for tenant '{TenantId}' on reconnect",
                        pool.Entity.Spec.PoolRtId, pool.Entity.Spec.TenantId);
                }
            }

            // Reverse-sync: tell the controller which pools we currently have
            // an active CR for so it can lift any DeploymentState that drifted
            // back to Pending (e.g. operator pod was restarted while the
            // controller stayed up, the CR survived in k8s but the controller's
            // in-memory tracking was lost). Cloud-only by hub contract — edge
            // operators would be rejected with a HubException. Workload-level
            // reverse-sync is NOT yet covered: the operator has no persistent
            // helm-release-to-workload-rtId mapping, so we report each pool
            // with an empty WorkloadRtIds list and rely on the controller's
            // own tracking + the existing PoolDeployedAsync fan-out for
            // workloads. Documented as a follow-up in CLAUDE.md.
            if (_options.AutoManagePools && ownedPools.Length > 0)
            {
                var reports = ownedPools
                    .Select(p => new OperatorDeployedPoolReportDto
                    {
                        TenantId = p.Entity.Spec.TenantId,
                        PoolRtId = p.Entity.Spec.PoolRtId,
                        // CR doesn't carry the human-readable name (it lives on
                        // the controller's RtPool.Name); the controller-side
                        // restore loads the name itself for log messages, so an
                        // empty value here is fine.
                        PoolName = string.Empty,
                        WorkloadRtIds = Array.Empty<string>(),
                    })
                    .ToArray();

                try
                {
                    await client.ReportDeployedStateAsync(reports);
                    _logger.LogInformation(
                        "Reverse-sync sent to controller: {Count} pool(s)",
                        reports.Length);
                }
                catch (Exception ex)
                {
                    // Self-healing is best-effort. Failing the report does not
                    // break ongoing operations — the next deploy / undeploy
                    // event will write the state correctly anyway. Log so the
                    // partial drift is at least diagnosable.
                    _logger.LogWarning(ex,
                        "Failed to send reverse-sync to controller ({Count} pool(s) skipped)",
                        reports.Length);
                }
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.StartAsync(onReconnect, stoppingToken);
                client.EnableReconnect(onReconnect);

                _logger.LogInformation("Operator hub connected, waiting for pool events");

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

        try
        {
            await client.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping operator hub client");
        }
    }

    public async Task PoolDeployedAsync(DeployedPoolDto pool)
    {
        _logger.LogInformation(
            "Pool deployed event received: tenant '{TenantId}', pool rtId {PoolRtId}",
            pool.TenantId, pool.PoolRtId);

        // Auto-CR-creation is the central-operator's job. Edge operators
        // receive the same broadcast (the controller fans out to every
        // connected operator) but must ignore it — CRs there are managed
        // out-of-band (manually or by an external system).
        if (!_options.AutoManagePools)
        {
            _logger.LogDebug(
                "AutoManagePools=false: not auto-creating CR for tenant '{TenantId}', pool rtId {PoolRtId}",
                pool.TenantId, pool.PoolRtId);
            return;
        }

        try
        {
            await _poolManager.CreatePoolAsync(pool.TenantId, pool.PoolRtId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create CommunicationPool CR for tenant '{TenantId}', pool rtId {PoolRtId}",
                pool.TenantId, pool.PoolRtId);
        }
    }

    public async Task PoolUndeployedAsync(string tenantId, string poolRtId)
    {
        _logger.LogInformation(
            "Pool undeployed event received: tenant '{TenantId}', pool rtId {PoolRtId}",
            tenantId, poolRtId);

        if (!_options.AutoManagePools)
        {
            _logger.LogDebug(
                "AutoManagePools=false: not auto-deleting CR for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
            return;
        }

        try
        {
            await _poolManager.DeletePoolAsync(tenantId, poolRtId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete CommunicationPool CR for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
        }
    }

    public async Task WorkloadDeployedAsync(WorkloadDeployedDto workload)
    {
        _logger.LogInformation(
            "Workload deployed event received: tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}', type '{WorkloadType}', chart '{ChartName}:{ChartVersion}'",
            workload.TenantId, workload.PoolRtId, workload.WorkloadName,
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
                "Failed to deploy workload '{WorkloadName}' for tenant '{TenantId}', pool rtId {PoolRtId}",
                workload.WorkloadName, workload.TenantId, workload.PoolRtId);
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

    public async Task WorkloadUndeployedAsync(WorkloadUndeployedDto workload)
    {
        _logger.LogInformation(
            "Workload undeployed event received: tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}', type '{WorkloadType}'",
            workload.TenantId, workload.PoolRtId, workload.WorkloadName, workload.WorkloadType);
        try
        {
            await _workloadReconciler.UndeployAsync(workload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to undeploy workload '{WorkloadName}' for tenant '{TenantId}', pool rtId {PoolRtId}",
                workload.WorkloadName, workload.TenantId, workload.PoolRtId);
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        _logger.LogInformation("Pre-update tenant event received: tenant '{TenantId}'", tenantId);
        try
        {
            var poolService = _serviceProvider.GetRequiredService<IPoolService>();
            if (poolService is IOperatorHubCallbacks_PreUpdateTenantHandler handler)
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
