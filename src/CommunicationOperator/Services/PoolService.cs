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
    // Keyed by (tenantId, poolRtId). PoolRtId is the canonical
    // controller-side pool identity (24-char hex MongoDB ObjectId);
    // a single operator can manage multiple CommunicationPool CRs from
    // different tenants without the keys colliding, and the key survives
    // a controller-side rename of the pool's display name.
    private readonly Dictionary<(string TenantId, string PoolRtId), Pool> _pools = new();
    private readonly object _gate = new();

    private static (string TenantId, string PoolRtId) KeyFor(V1CommunicationPoolEntity entity) =>
        (entity.Spec.TenantId, entity.Spec.PoolRtId);

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
        _logger.LogInformation("Registering pool rtId {PoolRtId} (tenant {TenantId})",
            entity.Spec.PoolRtId, entity.Spec.TenantId);

        Pool pool;
        var key = KeyFor(entity);
        lock (_gate)
        {
            if (!_pools.TryGetValue(key, out var existing))
            {
                existing = new Pool(new K8Pool
                {
                    Namespace = entity.Metadata?.NamespaceProperty ?? string.Empty,
                    TenantId = entity.Spec.TenantId,
                    PoolRtId = entity.Spec.PoolRtId,
                }, entity);
                _pools[key] = existing;
            }
            pool = existing;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // No-op when the hub connection is down — the reconnect
            // handler picks the pool up from GetPools() and re-registers
            // it then.
            await _hubInvoker.RegisterPoolAsync(entity.Spec.TenantId, entity.Spec.PoolRtId);
            pool.IsRegistered = _hubInvoker.IsConnected;
        }
        catch (HubException e)
        {
            throw PoolServiceException.ConnectionError(entity.Spec.PoolRtId, e);
        }
        catch (Exception e)
        {
            throw PoolServiceException.DeployFailed(entity.Spec.PoolRtId, e);
        }

        // Per-CR reverse-sync: even when the operator was already connected
        // (the bulk path in OperatorHubService.onReconnect ran), CRs that
        // KubeOps discovered AFTER that callback aren't in the bulk
        // snapshot and so wouldn't trigger restore of any drifted
        // DeploymentState. Fire a one-pool report here so every reconcile
        // self-heals. Cloud-mode-only enforced in the invoker; no-op when
        // the hub is down (the bulk path will catch up on the next
        // reconnect once GetPools() reflects this entry).
        if (pool.IsRegistered)
        {
            await _hubInvoker.ReportDeployedPoolAsync(entity.Spec.TenantId, entity.Spec.PoolRtId);
        }
    }

    public async Task UnRegisterPoolAsync(V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("Unregistering pool rtId {PoolRtId} (tenant {TenantId})",
            entity.Spec.PoolRtId, entity.Spec.TenantId);

        Pool? pool;
        var key = KeyFor(entity);
        lock (_gate)
        {
            if (!_pools.TryGetValue(key, out pool))
            {
                return;
            }
            _pools.Remove(key);
        }

        pool.IsRegistered = false;

        // Best-effort unregister at the controller. We're here because the
        // CommunicationPool CR is already gone, so the controller may also
        // have nothing to unregister (typical case during a tenant-delete
        // cascade). Treat HubException as a soft failure so the K8s
        // controller queue doesn't retry the delete reconcile forever.
        try
        {
            await _hubInvoker.UnregisterPoolAsync(entity.Spec.TenantId, entity.Spec.PoolRtId);
        }
        catch (HubException e)
        {
            _logger.LogWarning(e,
                "Controller refused unregister for pool rtId {PoolRtId} (likely tenant gone); local state cleared anyway",
                entity.Spec.PoolRtId);
        }
        catch (Exception e)
        {
            throw PoolServiceException.DeployFailed(entity.Spec.PoolRtId, e);
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
