using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// In-memory registry of <c>DeploymentSite</c> CRs the operator owns
/// plus the bridge that turns each CR reconcile into a hub-side
/// <c>RegisterDeploymentSiteAsync</c> / <c>UnregisterDeploymentSiteAsync</c> call.
///
/// The legacy implementation opened a separate <c>/deploymentSiteHub</c> SignalR
/// connection per deployment site. With the operator-hub now carrying the same
/// payload, we just keep the entity list in-memory and forward register /
/// unregister calls through <see cref="IOperatorHubInvoker"/>. On
/// reconnect <see cref="OperatorHubService"/> queries this service and
/// re-registers every deployment site through the same single connection.
/// </summary>
public class DeploymentSiteService : IDeploymentSiteService, IOperatorHubCallbacks_PreUpdateTenantHandler
{
    private readonly ILogger<DeploymentSiteService> _logger;
    private readonly IOperatorHubInvoker _hubInvoker;
    // Keyed by (tenantId, deploymentSiteRtId). DeploymentSiteRtId is the canonical
    // controller-side deployment site identity (24-char hex MongoDB ObjectId);
    // a single operator can manage multiple DeploymentSite CRs from
    // different tenants without the keys colliding, and the key survives
    // a controller-side rename of the deployment site's display name.
    private readonly Dictionary<(string TenantId, string DeploymentSiteRtId), DeploymentSite> _deploymentSites = new();
    private readonly object _gate = new();

    private static (string TenantId, string DeploymentSiteRtId) KeyFor(V1DeploymentSiteEntity entity) =>
        (entity.Spec.TenantId, entity.Spec.DeploymentSiteRtId);

    public DeploymentSiteService(ILogger<DeploymentSiteService> logger, IOperatorHubInvoker hubInvoker)
    {
        _logger = logger;
        _hubInvoker = hubInvoker;
    }

    /// <summary>
    /// Snapshot of every deployment site the operator currently owns. Used by the
    /// operator hub's reconnect callback to replay <c>RegisterDeploymentSiteAsync</c>
    /// for each one.
    /// </summary>
    public IReadOnlyCollection<DeploymentSite> GetDeploymentSites()
    {
        lock (_gate)
        {
            return _deploymentSites.Values.ToArray();
        }
    }

    /// <summary>
    /// Marks all owned deployment sites as not-yet-registered. The operator-hub
    /// service calls this when its SignalR connection drops so the next
    /// reconnect cycle re-runs registration for every deployment site.
    /// </summary>
    public void ResetRegistrationState()
    {
        lock (_gate)
        {
            foreach (var deploymentSite in _deploymentSites.Values)
            {
                deploymentSite.IsRegistered = false;
            }
        }
    }

    public async Task RegisterDeploymentSiteAsync(V1DeploymentSiteEntity entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering deployment site rtId {DeploymentSiteRtId} (tenant {TenantId})",
            entity.Spec.DeploymentSiteRtId, entity.Spec.TenantId);

        DeploymentSite deploymentSite;
        var key = KeyFor(entity);
        lock (_gate)
        {
            if (!_deploymentSites.TryGetValue(key, out var existing))
            {
                existing = new DeploymentSite(new K8DeploymentSite
                {
                    Namespace = entity.Metadata?.NamespaceProperty ?? string.Empty,
                    TenantId = entity.Spec.TenantId,
                    DeploymentSiteRtId = entity.Spec.DeploymentSiteRtId,
                }, entity);
                _deploymentSites[key] = existing;
            }
            deploymentSite = existing;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // No-op when the hub connection is down — the reconnect
            // handler picks the deployment site up from GetDeploymentSites() and re-registers
            // it then.
            await _hubInvoker.RegisterDeploymentSiteAsync(entity.Spec.TenantId, entity.Spec.DeploymentSiteRtId);
            deploymentSite.IsRegistered = _hubInvoker.IsConnected;
        }
        catch (HubException e)
        {
            throw DeploymentSiteServiceException.ConnectionError(entity.Spec.DeploymentSiteRtId, e);
        }
        catch (Exception e)
        {
            throw DeploymentSiteServiceException.DeployFailed(entity.Spec.DeploymentSiteRtId, e);
        }

        // Per-CR reverse-sync: even when the operator was already connected
        // (the bulk path in OperatorHubService.onReconnect ran), CRs that
        // KubeOps discovered AFTER that callback aren't in the bulk
        // snapshot and so wouldn't trigger restore of any drifted
        // DeploymentState. Fire a one-pool report here so every reconcile
        // self-heals. Cloud-mode-only enforced in the invoker; no-op when
        // the hub is down (the bulk path will catch up on the next
        // reconnect once GetDeploymentSites() reflects this entry).
        if (deploymentSite.IsRegistered)
        {
            await _hubInvoker.ReportDeployedDeploymentSiteAsync(entity.Spec.TenantId, entity.Spec.DeploymentSiteRtId);
        }
    }

    public async Task UnRegisterDeploymentSiteAsync(V1DeploymentSiteEntity entity)
    {
        _logger.LogInformation("Unregistering deployment site rtId {DeploymentSiteRtId} (tenant {TenantId})",
            entity.Spec.DeploymentSiteRtId, entity.Spec.TenantId);

        DeploymentSite? deploymentSite;
        var key = KeyFor(entity);
        lock (_gate)
        {
            if (!_deploymentSites.TryGetValue(key, out deploymentSite))
            {
                return;
            }
            _deploymentSites.Remove(key);
        }

        deploymentSite.IsRegistered = false;

        // Best-effort unregister at the controller. We're here because the
        // DeploymentSite CR is already gone, so the controller may also
        // have nothing to unregister (typical case during a tenant-delete
        // cascade). Treat HubException as a soft failure so the K8s
        // controller queue doesn't retry the delete reconcile forever.
        try
        {
            await _hubInvoker.UnregisterDeploymentSiteAsync(entity.Spec.TenantId, entity.Spec.DeploymentSiteRtId);
        }
        catch (HubException e)
        {
            _logger.LogWarning(e,
                "Controller refused unregister for deployment site rtId {DeploymentSiteRtId} (likely tenant gone); local state cleared anyway",
                entity.Spec.DeploymentSiteRtId);
        }
        catch (Exception e)
        {
            throw DeploymentSiteServiceException.DeployFailed(entity.Spec.DeploymentSiteRtId, e);
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        _logger.LogInformation("PreUpdateTenantAsync for tenant {TenantId}", tenantId);

        try
        {
            DeploymentSite[] tenantDeploymentSites;
            lock (_gate)
            {
                tenantDeploymentSites = _deploymentSites.Values
                    .Where(p => p.Entity.Spec.TenantId == tenantId)
                    .ToArray();
            }

            foreach (var deploymentSite in tenantDeploymentSites)
            {
                await UnRegisterDeploymentSiteAsync(deploymentSite.Entity);
            }

            _logger.LogInformation("Waiting for 5 seconds before re-registering deployment sites");
            await Task.Delay(5000);
            _logger.LogInformation("Re-registering deployment sites");

            foreach (var deploymentSite in tenantDeploymentSites)
            {
                await RegisterDeploymentSiteAsync(deploymentSite.Entity, CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            throw DeploymentSiteServiceException.PreUpdateTenantFailed(tenantId, e);
        }
    }
}

/// <summary>
/// Marker interface for the pre-update tenant callback. Implemented by
/// <see cref="DeploymentSiteService"/>; consumed by <see cref="OperatorHubService"/>
/// which forwards the operator-hub <c>PreUpdateTenantAsync</c> event into
/// the deployment site registry's reconnect cycle.
/// </summary>
public interface IOperatorHubCallbacks_PreUpdateTenantHandler
{
    Task PreUpdateTenantAsync(string tenantId);
}
