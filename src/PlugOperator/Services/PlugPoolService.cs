using k8s.Models;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.PlugControllerServices;
using PlugOperator.Entities;
using PlugOperator.Models;
using PlugOperator.Reconcilers;

namespace PlugOperator.Services;

public class PlugPoolService : IPlugPoolService, IPoolHubCallbacks
{
    private readonly ILogger<PlugPoolService> _logger;
    private readonly IPlugReconciler _plugReconciler;
    private readonly Dictionary<string, Pool> _pools = new();

    public PlugPoolService(ILogger<PlugPoolService> logger, IPlugReconciler plugReconciler)
    {
        _logger = logger;
        _plugReconciler = plugReconciler;
    }
    
    public async Task RegisterPoolAsync(V1PlugPoolEntity entity)
    {
        if (_pools.TryGetValue(entity.Spec.PlugPoolName, out var pool) && pool.PlugPoolControllerClient.IsAlive)
        {
            return;
        }
        
        var plugPoolControllerClient = new PlugPoolControllerClient(new PlugControllerClientOptions
        {
            EndpointUri = entity.Spec.PlugControllerUri,
            TenantId = entity.Spec.TenantId
        }, new ServiceClientAccessToken(), this);
        
        pool = new Pool(new PoolDescriptor
        {
            Namespace = entity.Namespace(),
            TenantId = entity.Spec.TenantId,
            PoolName = entity.Spec.PlugPoolName,
            PlugControllerUri = entity.Spec.PlugControllerUri,
            BrokerHost = entity.Spec.BrokerHost,
            BrokerVirtualHost = string.IsNullOrWhiteSpace(entity.Spec.BrokerVirtualHost) ? "/" : entity.Spec.BrokerVirtualHost,
            BrokerPort = entity.Spec.BrokerPort,
        }, plugPoolControllerClient, entity);

        _pools[entity.Spec.PlugPoolName] = pool;

        await DeleteDeploymentAsync(entity);
        
        try
        {
            await plugPoolControllerClient.StartAsync();
        
            var plugPoolConfiguration = await plugPoolControllerClient.RegisterPlugPoolOperatorAsync(entity.Spec.PlugPoolName);
            foreach (var plug in plugPoolConfiguration.Plugs)
            {
                await DeployPlug(pool.PoolDescriptor, plug, entity);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error connecting to plug controller");
        }
    }

    public async Task UnRegisterPoolAsync(V1PlugPoolEntity entity)
    {
        if (_pools.ContainsKey(entity.Spec.PlugPoolName))
        {
            var pool = _pools[entity.Spec.PlugPoolName];
            await pool.PlugPoolControllerClient.UnregisterPlugPoolOperatorAsync(entity.Spec.PlugPoolName);
            await pool.PlugPoolControllerClient.StopAsync();
            _pools.Remove(entity.Spec.PlugPoolName);
        }

        await DeleteDeploymentAsync(entity);
    }

    private async Task DeleteDeploymentAsync(V1PlugPoolEntity entity)
    {
        await _plugReconciler.DeleteAsync(new K8Pool
        {
            Namespace = entity.Namespace(),
            PoolName = entity.Spec.PlugPoolName,
            TenantId = entity.Spec.TenantId
        });
    }

    private async Task DeployPlug(PoolDescriptor poolDescriptor, PlugPoolPlugDto poolPlugDto , V1PlugPoolEntity entity)
    {
        await _plugReconciler.ReconcileAsync(poolDescriptor, poolPlugDto, entity);
    }

    public async Task DeployPlugAsync(string tenantId, PlugPoolPlugDto plug)
    {
        if (_pools.TryGetValue(plug.PlugPoolName, out var pool))
        {
            await DeployPlug(pool.PoolDescriptor, plug, pool.Entity);
        }
    }

    public async Task UndeployPlugAsync(string tenantId, PlugPoolPlugDto plug)
    {
        if (_pools.TryGetValue(plug.PlugPoolName, out var pool))
        {
            await _plugReconciler.DeleteAsync(pool.PoolDescriptor, plug);
        }
    }
}