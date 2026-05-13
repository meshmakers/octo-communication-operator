using k8s;
using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class PoolService(
    ILogger<PoolService> logger,
    ILoggerFactory loggerFactory)
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
            var poolHubClientOptions = new PoolHubClientOptions
            {
                EndpointUri = entity.Spec.CommunicationControllerUri,
                TenantId = entity.Spec.TenantId,
                PoolName = entity.Spec.PoolName
            };
            var poolHubClient = new PoolHubClient(poolHubClientOptions, loggerFactory.CreateLogger<PoolHubClient>(),
                new ServiceClientAccessToken(), this);

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
                InstancePrefix = entity.Spec.InstancePrefix,
                IgnoreCertificateValidation = entity.Spec.IgnoreCertificateValidation
            }, poolHubClient, entity);

            _pools[entity.Spec.PoolName] = pool;

            cancellationToken.ThrowIfCancellationRequested();
            var onReconnectFunction = async (bool isReconnect) =>
            {
                logger.LogInformation("Registering pool {PoolName}", entity.Spec.PoolName);
                await poolHubClient.RegisterPoolOperatorAsync(entity.Spec.PoolName);
                pool.IsRegistered = true;
                logger.LogInformation(
                    "Registered pool {PoolName} with controller; workloads (if any) are delivered via the operator hub's WorkloadDeployedAsync callback",
                    entity.Spec.PoolName);
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

        if (!_pools.ContainsKey(entity.Spec.PoolName))
        {
            return;
        }

        try
        {
            var pool = _pools[entity.Spec.PoolName];
            pool.IsRegistered = false;

            // Best-effort unregister at the controller. We're here because the
            // CommunicationPool CR is already gone, so the controller may also
            // have nothing to unregister: typical case is the tenant-delete
            // cascade, where the tenant doesn't exist anymore by the time we
            // call back and the hub answers with TenantException. Treat any
            // HubException as a soft failure — still close the local connection
            // and clear our cache entry, otherwise the KubeOps queue would
            // retry the delete reconcile until it gives up.
            try
            {
                await pool.PoolHubClient.UnregisterPoolOperatorAsync(entity.Spec.PoolName);
            }
            catch (HubException e)
            {
                logger.LogWarning(e,
                    "Controller refused unregister for pool {PoolName} (likely tenant gone); closing connection anyway",
                    entity.Spec.PoolName);
            }

            try
            {
                await pool.PoolHubClient.StopAsync();
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "Failed to stop pool hub client for {PoolName}; ignoring",
                    entity.Spec.PoolName);
            }
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
