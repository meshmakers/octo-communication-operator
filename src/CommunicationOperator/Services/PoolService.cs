using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class PoolService : IPoolService, IPoolHubCallbacks
{
    private readonly ILogger<PoolService> _logger;
    private readonly ICommunicationAdapterReconciler _communicationAdapterReconciler;
    private readonly Dictionary<string, Pool> _pools = new();

    public PoolService(ILogger<PoolService> logger, ICommunicationAdapterReconciler communicationAdapterReconciler)
    {
        _logger = logger;
        _communicationAdapterReconciler = communicationAdapterReconciler;
    }

    public async Task RegisterPoolAsync(V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("Registering pool {PoolName}", entity.Spec.PoolName);
        try
        {
            if (_pools.TryGetValue(entity.Spec.PoolName, out var pool))
            {
                if (pool.PoolHubClient.IsAlive && pool.IsRegistered)
                {
                    _logger.LogInformation("Pool {PoolName} already registered, connection alive", entity.Spec.PoolName);
                    return;
                }

                await pool.PoolHubClient.StopAsync();
                _pools.Remove(entity.Spec.PoolName);
            }

            _logger.LogInformation("Registering pool {PoolName} with controller {ControllerUri}", entity.Spec.PoolName,
                entity.Spec.CommunicationControllerUri);
            var controllerClient = new PoolHubClient(new PoolHubClientOptions
            {
                EndpointUri = entity.Spec.CommunicationControllerUri,
                TenantId = entity.Spec.TenantId,
                PoolName = entity.Spec.PoolName
            }, new ServiceClientAccessToken(), this);

            pool = new Pool(new PoolDescriptor
            {
                Namespace = entity.Namespace(),
                TenantId = entity.Spec.TenantId,
                PoolName = entity.Spec.PoolName,
                ControllerUri = entity.Spec.CommunicationControllerUri,
                BrokerHost = entity.Spec.BrokerHost,
                BrokerVirtualHost = string.IsNullOrWhiteSpace(entity.Spec.BrokerVirtualHost) ? "/" : entity.Spec.BrokerVirtualHost,
                BrokerPort = entity.Spec.BrokerPort,
            }, controllerClient, entity);

            _pools[entity.Spec.PoolName] = pool;

            _logger.LogInformation("Deleting deployment for pool {PoolName}", entity.Spec.PoolName);
            await DeleteDeploymentAsync(entity);


            _logger.LogInformation("Starting pool {PoolName}", entity.Spec.PoolName);
            await controllerClient.StartAsync();

            _logger.LogInformation("Registering pool {PoolName} with controller", entity.Spec.PoolName);
            var poolConfiguration = await controllerClient.RegisterPoolOperatorAsync(entity.Spec.PoolName);
            _logger.LogInformation("Registered pool {PoolName} with controller, configuration of '{AdapterCount}' adapter retrieved",
                entity.Spec.PoolName, poolConfiguration.CommunicationAdapterList.Count());
            pool.IsRegistered = true;

            foreach (var adapterDto in poolConfiguration.CommunicationAdapterList)
            {
                await DeployAdapter(pool.PoolDescriptor, adapterDto, entity);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error connecting to communication controller");
        }
    }

    public async Task UnRegisterPoolAsync(V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("Unregistering pool {PoolName}", entity.Spec.PoolName);

        if (_pools.ContainsKey(entity.Spec.PoolName))
        {
            try
            {
                var pool = _pools[entity.Spec.PoolName];
                await pool.PoolHubClient.UnregisterPoolOperatorAsync(entity.Spec.PoolName);
                await pool.PoolHubClient.StopAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error unregistering at communication controller");
                throw;
            }
            finally
            {
                _pools.Remove(entity.Spec.PoolName);
            }
        }

        await DeleteDeploymentAsync(entity);
    }

    private async Task DeleteDeploymentAsync(V1CommunicationPoolEntity entity)
    {
        await _communicationAdapterReconciler.DeleteAsync(new K8Pool
        {
            Namespace = entity.Namespace(),
            PoolName = entity.Spec.PoolName,
            TenantId = entity.Spec.TenantId
        });
    }

    private async Task DeployAdapter(PoolDescriptor poolDescriptor, PoolCommunicationAdapterDto adapterDto, V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("Deploying adapter '{AdapterCkId}/{AdapterRtId}‘ for pool {PoolName}", adapterDto.AdapterCkTypeId,
            adapterDto.AdapterRtId, poolDescriptor.PoolName);

        await _communicationAdapterReconciler.ReconcileAsync(poolDescriptor, adapterDto, entity);
    }

    public async Task DeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto adapterDto)
    {
        _logger.LogInformation("Deploying adapter '{AdapterCkId}/{AdapterRtId}‘ for pool {PoolName}", adapterDto.AdapterCkTypeId,
            adapterDto.AdapterRtId, adapterDto.PoolName);

        if (_pools.TryGetValue(adapterDto.PoolName, out var pool))
        {
            await DeployAdapter(pool.PoolDescriptor, adapterDto, pool.Entity);
        }
    }

    public async Task UndeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto adapterDto)
    {
        _logger.LogInformation("Undeploying adapter '{AdapterCkId}/{AdapterRtId}‘ for pool {PoolName}", adapterDto.AdapterCkTypeId,
            adapterDto.AdapterRtId, adapterDto.PoolName);

        if (_pools.TryGetValue(adapterDto.PoolName, out var pool))
        {
            await _communicationAdapterReconciler.DeleteAsync(pool.PoolDescriptor, adapterDto);
        }
    }
}