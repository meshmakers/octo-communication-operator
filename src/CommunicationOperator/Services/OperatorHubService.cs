using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Background service that maintains a SignalR management connection to the Communication Controller.
/// Receives Cloud pool deploy / undeploy events and creates/deletes CommunicationPool CRs accordingly.
/// Only active when AutoManagePools is enabled.
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
    // Stays null when AutoManagePools is disabled (the service returns early
    // and never builds a client).
    private IOperatorHubClient? _client;

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
    public async Task RegisterPoolAsync(string tenantId, string poolName)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping RegisterPoolAsync for tenant '{TenantId}', pool '{PoolName}' (will be replayed on reconnect)",
                tenantId, poolName);
            return;
        }
        await client.RegisterPoolAsync(tenantId, poolName);
    }

    /// <inheritdoc />
    public async Task UnregisterPoolAsync(string tenantId, string poolName)
    {
        var client = _client;
        if (client == null || !client.IsAlive)
        {
            _logger.LogDebug(
                "Operator-hub not connected; skipping UnregisterPoolAsync for tenant '{TenantId}', pool '{PoolName}'",
                tenantId, poolName);
            return;
        }
        await client.UnregisterPoolAsync(tenantId, poolName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoManagePools)
        {
            _logger.LogInformation("AutoManagePools is disabled, operator hub service will not start");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.CommunicationControllerUri))
        {
            _logger.LogError("CommunicationControllerUri is required when AutoManagePools is enabled");
            return;
        }

        _logger.LogInformation(
            "Starting operator hub service, connecting to controller at {ControllerUri}",
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
            var deployedPools = (await client.RegisterOperatorAsync()).ToArray();
            _logger.LogInformation("Registered with controller, {PoolCount} deployed Cloud pools",
                deployedPools.Length);

            foreach (var pool in deployedPools)
            {
                await _poolManager.CreatePoolAsync(pool.TenantId, pool.PoolName);
            }

            // Replay pool registrations for every CommunicationPool CR the
            // operator currently owns. On a fresh connect this is empty (CRs
            // arrive via PoolDeployedAsync afterwards), on a reconnect this
            // is what flips every pool back to Online.
            var poolService = _serviceProvider.GetRequiredService<IPoolService>();
            foreach (var pool in poolService.GetPools())
            {
                try
                {
                    await client.RegisterPoolAsync(pool.Entity.Spec.TenantId, pool.Entity.Spec.PoolName);
                    pool.IsRegistered = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to re-register pool '{PoolName}' for tenant '{TenantId}' on reconnect",
                        pool.Entity.Spec.PoolName, pool.Entity.Spec.TenantId);
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
        _logger.LogInformation("Pool deployed event received: tenant '{TenantId}', pool '{PoolName}'",
            pool.TenantId, pool.PoolName);
        try
        {
            await _poolManager.CreatePoolAsync(pool.TenantId, pool.PoolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create CommunicationPool CR for tenant '{TenantId}', pool '{PoolName}'",
                pool.TenantId, pool.PoolName);
        }
    }

    public async Task PoolUndeployedAsync(string tenantId, string poolName)
    {
        _logger.LogInformation("Pool undeployed event received: tenant '{TenantId}', pool '{PoolName}'",
            tenantId, poolName);
        try
        {
            await _poolManager.DeletePoolAsync(tenantId, poolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to delete CommunicationPool CR for tenant '{TenantId}', pool '{PoolName}'",
                tenantId, poolName);
        }
    }

    public async Task WorkloadDeployedAsync(WorkloadDeployedDto workload)
    {
        _logger.LogInformation(
            "Workload deployed event received: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', type '{WorkloadType}', chart '{ChartName}:{ChartVersion}'",
            workload.TenantId, workload.PoolName, workload.WorkloadName,
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
                "Failed to deploy workload '{WorkloadName}' for tenant '{TenantId}', pool '{PoolName}'",
                workload.WorkloadName, workload.TenantId, workload.PoolName);
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
            PoolName = workload.PoolName,
            WorkloadName = workload.WorkloadName,
            WorkloadRtId = workload.WorkloadRtId,
            Success = success,
            StatusMessage = statusMessage,
        });
    }

    public async Task WorkloadUndeployedAsync(WorkloadUndeployedDto workload)
    {
        _logger.LogInformation(
            "Workload undeployed event received: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', type '{WorkloadType}'",
            workload.TenantId, workload.PoolName, workload.WorkloadName, workload.WorkloadType);
        try
        {
            await _workloadReconciler.UndeployAsync(workload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to undeploy workload '{WorkloadName}' for tenant '{TenantId}', pool '{PoolName}'",
                workload.WorkloadName, workload.TenantId, workload.PoolName);
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
