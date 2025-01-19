using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class PoolService(ILogger<PoolService> logger, IAdapterReconciler adapterReconciler)
    : IPoolService, IPoolHubCallbacks
{
    private readonly Dictionary<string, Pool> _pools = new();

    public async Task RegisterPoolAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Registering pool {PoolName}", entity.Spec.PoolName);
        try
        {
            if (_pools.TryGetValue(entity.Spec.PoolName, out var pool))
            {
                if (pool.PoolHubClient.IsAlive && pool.IsRegistered)
                {
                    logger.LogInformation("Pool {PoolName} already registered, connection alive", entity.Spec.PoolName);
                    return;
                }

                await pool.PoolHubClient.StopAsync();
                _pools.Remove(entity.Spec.PoolName);
            }
            
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation("Registering pool {PoolName} with controller {ControllerUri}", entity.Spec.PoolName,
                entity.Spec.CommunicationControllerUri);
            var poolHubClient = new PoolHubClient(new PoolHubClientOptions
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
                BrokerVirtualHost = string.IsNullOrWhiteSpace(entity.Spec.BrokerVirtualHost)
                    ? "/"
                    : entity.Spec.BrokerVirtualHost,
                BrokerPort = entity.Spec.BrokerPort,
            }, poolHubClient, entity);

            _pools[entity.Spec.PoolName] = pool;

            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Deleting deployment for pool {PoolName}", entity.Spec.PoolName);
            await DeleteDeploymentAsync(entity);

            cancellationToken.ThrowIfCancellationRequested();
            var onReconnectFunction = async (bool isReconnect) =>
            {
                logger.LogInformation("Registering pool {PoolName}", entity.Spec.PoolName);
                var poolConfiguration = await poolHubClient.RegisterPoolOperatorAsync(entity.Spec.PoolName);
                logger.LogInformation(
                    "Registered pool {PoolName} with controller, configuration of '{AdapterCount}' adapter retrieved",
                    entity.Spec.PoolName, poolConfiguration.CommunicationAdapterList.Count());
                pool.IsRegistered = true;
                    
                foreach (var adapterDto in poolConfiguration.CommunicationAdapterList)
                {
                    await DeployAdapterAsync(pool, adapterDto, entity);
                }
            };

            logger.LogInformation("Starting pool {PoolName} at controller {Uri}", entity.Spec.PoolName, 
                entity.Spec.CommunicationControllerUri);
            await poolHubClient.StartAsync(onReconnectFunction, CancellationToken.None);
            poolHubClient.EnableReconnect(onReconnectFunction);
        }
        catch (HubException e)
        {
            throw PoolServiceException.ConnectionError(entity.Spec.PoolName, e);
        }
        catch (Exception e)
        {
            throw PoolServiceException.DeployFailed(entity.Spec.PoolName, e);
        }
    }

    public async Task UnRegisterPoolAsync(V1CommunicationPoolEntity entity)
    {
        logger.LogInformation("Unregistering pool {PoolName}", entity.Spec.PoolName);

        if (_pools.ContainsKey(entity.Spec.PoolName))
        {
            try
            {
                await DeleteDeploymentAsync(entity);

                var pool = _pools[entity.Spec.PoolName];
                pool.IsRegistered = false;
                await pool.PoolHubClient.UnregisterPoolOperatorAsync(entity.Spec.PoolName);
                await pool.PoolHubClient.StopAsync();
            }
            catch (HubException e)
            {
                throw PoolServiceException.ConnectionError(entity.Spec.PoolName, e);
            }
            catch (Exception e)
            {
                throw PoolServiceException.DeployFailed(entity.Spec.PoolName, e);
            }
            finally
            {
                _pools.Remove(entity.Spec.PoolName);
            }
        }

    }

    private async Task DeleteDeploymentAsync(V1CommunicationPoolEntity entity)
    {
        await adapterReconciler.DeleteAsync(new K8Pool
        {
            Namespace = entity.Namespace(),
            PoolName = entity.Spec.PoolName,
            TenantId = entity.Spec.TenantId
        });
    }

    private async Task DeployAdapterAsync(Pool pool, PoolCommunicationAdapterDto adapterDto,
        V1CommunicationPoolEntity entity)
    {
        logger.LogInformation("Deploying adapter '{AdapterRtEntityId}‘ for pool {PoolName}",
            adapterDto.AdapterRtEntityId, pool.PoolDescriptor.PoolName);

        await adapterReconciler.ReconcileAsync(pool, adapterDto, entity);
    }

    public async Task UpdatePoolConfigurationAsync(string tenantId, string poolName,
        PoolConfigurationDto poolConfigurationDto)
    {
        logger.LogInformation("Updating pool configuration for tenant '{TenantId}', pool '{PoolName}'", tenantId,
            poolName);

        if (_pools.TryGetValue(poolName, out var pool))
        {
            await adapterReconciler.DeleteAsync(new K8Pool
            {
                Namespace = pool.PoolDescriptor.Namespace,
                PoolName = pool.PoolDescriptor.PoolName,
                TenantId = pool.PoolDescriptor.TenantId
            });

            foreach (var adapterDto in poolConfigurationDto.CommunicationAdapterList)
            {
                await DeployAdapterAsync(pool, adapterDto, pool.Entity);
            }
        }
    }

    public async Task DeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto adapterDto)
    {
        logger.LogInformation("Deploying adapter '{AdapterRtEntityId}‘ for pool {PoolName}",
            adapterDto.AdapterRtEntityId, adapterDto.PoolName);

        if (_pools.TryGetValue(adapterDto.PoolName, out var pool))
        {
            await DeployAdapterAsync(pool, adapterDto, pool.Entity);
        }
    }

    public async Task UndeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto adapterDto)
    {
        logger.LogInformation("Undeploying adapter '{AdapterRtEntityId}‘ for pool {PoolName}",
            adapterDto.AdapterRtEntityId, adapterDto.PoolName);

        if (_pools.TryGetValue(adapterDto.PoolName, out var pool))
        {
            await adapterReconciler.DeleteAsync(pool, adapterDto);
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        logger.LogInformation("PreUpdateTenantAsync for tenant {TenantId}", tenantId);

        try
        {
            var pools = _pools.Values.ToArray();
            foreach (var pool in pools)
            {
                await UnRegisterPoolAsync(pool.Entity);
            }
        
            logger.LogInformation("Waiting for 5 seconds before re-registering pools");
            await Task.Delay(5000);
            logger.LogInformation("Re-registering pools");
        
            foreach (var pool in pools)
            {
                await RegisterPoolAsync(pool.Entity, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.PreUpdateTenantFailed(tenantId, e);
        }
    }
}