using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// In-memory registry of <c>CommunicationPool</c> CRs the operator owns
/// plus the bridge that turns each CR reconcile into a hub-side
/// <c>RegisterPoolAsync</c> / <c>UnregisterPoolAsync</c> call.
///
/// The legacy implementation opened a separate <c>/poolHub</c> SignalR
/// connection per pool. With the operator-hub now carrying the same
/// payload, we just keep the entity list in-memory and forward register /
/// unregister calls through <see cref="IOperatorHubInvoker"/>. On
/// reconnect <see cref="OperatorHubService"/> queries this service and
/// re-registers every pool through the same single connection.
/// </summary>
public class PoolService : IPoolService, IOperatorHubCallbacks_PreUpdateTenantHandler
{
    private readonly ILogger<PoolService> _logger;
    private readonly IOperatorHubInvoker _hubInvoker;
    private readonly Dictionary<string, Pool> _pools = new();
    private readonly object _gate = new();

    public PoolService(ILogger<PoolService> logger, IOperatorHubInvoker hubInvoker)
    {
        _logger = logger;
        _hubInvoker = hubInvoker;
    }

    /// <summary>
    /// Snapshot of every pool the operator currently owns. Used by the
    /// operator hub's reconnect callback to replay <c>RegisterPoolAsync</c>
    /// for each one.
    /// </summary>
    public IReadOnlyCollection<Pool> GetPools()
    {
        lock (_gate)
        {
            return _pools.Values.ToArray();
        }
    }

    /// <summary>
    /// Marks all owned pools as not-yet-registered. The operator-hub
    /// service calls this when its SignalR connection drops so the next
    /// reconnect cycle re-runs registration for every pool.
    /// </summary>
    public void ResetRegistrationState()
    {
        lock (_gate)
        {
            foreach (var pool in _pools.Values)
            {
                pool.IsRegistered = false;
            }
        }
    }

    public async Task RegisterPoolAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering pool {PoolName} (tenant {TenantId})",
            entity.Spec.PoolName, entity.Spec.TenantId);

        Pool pool;
        lock (_gate)
        {
            if (!_pools.TryGetValue(entity.Spec.PoolName, out var existing))
            {
                existing = new Pool(new PoolDescriptor
                {
                    Namespace = entity.Metadata?.NamespaceProperty ?? string.Empty,
                    TenantId = entity.Spec.TenantId,
                    PoolName = entity.Spec.PoolName,
                    ControllerUri = entity.Spec.CommunicationControllerUri,
                    BrokerHost = entity.Spec.BrokerHost,
                    BrokerVirtualHost = string.IsNullOrWhiteSpace(entity.Spec.BrokerVirtualHost)
                        ? "/"
                        : entity.Spec.BrokerVirtualHost,
                    BrokerPort = entity.Spec.BrokerPort,
                    InstancePrefix = entity.Spec.InstancePrefix,
                    IgnoreCertificateValidation = entity.Spec.IgnoreCertificateValidation,
                }, entity);
                _pools[entity.Spec.PoolName] = existing;
            }
            pool = existing;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // No-op when the hub connection is down — the reconnect
            // handler picks the pool up from GetPools() and re-registers
            // it then.
            await _hubInvoker.RegisterPoolAsync(entity.Spec.TenantId, entity.Spec.PoolName);
            pool.IsRegistered = _hubInvoker.IsConnected;
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
        _logger.LogInformation("Unregistering pool {PoolName} (tenant {TenantId})",
            entity.Spec.PoolName, entity.Spec.TenantId);

        Pool? pool;
        lock (_gate)
        {
            if (!_pools.TryGetValue(entity.Spec.PoolName, out pool))
            {
                return;
            }
            _pools.Remove(entity.Spec.PoolName);
        }

        pool.IsRegistered = false;

        // Best-effort unregister at the controller. We're here because the
        // CommunicationPool CR is already gone, so the controller may also
        // have nothing to unregister (typical case during a tenant-delete
        // cascade). Treat HubException as a soft failure so the K8s
        // controller queue doesn't retry the delete reconcile forever.
        try
        {
            await _hubInvoker.UnregisterPoolAsync(entity.Spec.TenantId, entity.Spec.PoolName);
        }
        catch (HubException e)
        {
            _logger.LogWarning(e,
                "Controller refused unregister for pool {PoolName} (likely tenant gone); local state cleared anyway",
                entity.Spec.PoolName);
        }
        catch (Exception e)
        {
            throw PoolServiceException.DeployFailed(entity.Spec.PoolName, e);
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        _logger.LogInformation("PreUpdateTenantAsync for tenant {TenantId}", tenantId);

        try
        {
            Pool[] tenantPools;
            lock (_gate)
            {
                tenantPools = _pools.Values
                    .Where(p => p.Entity.Spec.TenantId == tenantId)
                    .ToArray();
            }

            foreach (var pool in tenantPools)
            {
                await UnRegisterPoolAsync(pool.Entity);
            }

            _logger.LogInformation("Waiting for 5 seconds before re-registering pools");
            await Task.Delay(5000);
            _logger.LogInformation("Re-registering pools");

            foreach (var pool in tenantPools)
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

/// <summary>
/// Marker interface for the pre-update tenant callback. Implemented by
/// <see cref="PoolService"/>; consumed by <see cref="OperatorHubService"/>
/// which forwards the operator-hub <c>PreUpdateTenantAsync</c> event into
/// the pool registry's reconnect cycle.
/// </summary>
public interface IOperatorHubCallbacks_PreUpdateTenantHandler
{
    Task PreUpdateTenantAsync(string tenantId);
}
