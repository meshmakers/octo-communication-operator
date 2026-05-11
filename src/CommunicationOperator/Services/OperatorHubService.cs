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
public class OperatorHubService : BackgroundService, IOperatorHubCallbacks
{
    private readonly ILogger<OperatorHubService> _logger;
    private readonly OperatorOptions _options;
    private readonly IOperatorHubClientFactory _clientFactory;
    private readonly ICommunicationPoolManager _poolManager;
    private readonly IWorkloadReconciler _workloadReconciler;

    public OperatorHubService(
        ILogger<OperatorHubService> logger,
        IOptions<OperatorOptions> options,
        IOperatorHubClientFactory clientFactory,
        ICommunicationPoolManager poolManager,
        IWorkloadReconciler workloadReconciler)
    {
        _logger = logger;
        _options = options.Value;
        _clientFactory = clientFactory;
        _poolManager = poolManager;
        _workloadReconciler = workloadReconciler;
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
        try
        {
            await _workloadReconciler.DeployAsync(workload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Don't let a single bad workload crash the hub connection.
            _logger.LogError(ex,
                "Failed to deploy workload '{WorkloadName}' for tenant '{TenantId}', pool '{PoolName}'",
                workload.WorkloadName, workload.TenantId, workload.PoolName);
        }
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
}
