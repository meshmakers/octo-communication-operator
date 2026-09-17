using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Thin client-side wrapper around the single <c>/operatorHub</c> connection.
/// Exists so non-hosted services (currently <see cref="DeploymentSiteService"/>) can
/// invoke hub methods without taking a dependency on
/// <see cref="OperatorHubService"/> — the latter holds the connection's
/// lifecycle.
///
/// All methods are no-ops when <see cref="IsConnected"/> is <c>false</c>;
/// the operator's reconnect handler reads the local deployment site list and replays
/// any missed Register calls once the connection comes back.
/// </summary>
public interface IOperatorHubInvoker
{
    /// <summary>
    /// <c>true</c> while the operator-hub SignalR connection is alive.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Invokes <c>IOperatorHub.RegisterDeploymentSiteAsync</c> on the controller for
    /// the given deployment site. Silently skips when the connection is down.
    /// <paramref name="deploymentSiteRtId"/> is the controller-side lookup key.
    /// </summary>
    Task RegisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Invokes <c>IOperatorHub.UnregisterDeploymentSiteAsync</c>. Silently skips when
    /// the connection is down — the deployment site will be reset to <c>Offline</c>
    /// the next time the operator-hub disconnects or when the next
    /// reconnect runs without that deployment site in the local list.
    /// </summary>
    Task UnregisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Per-pool variant of the reverse-sync that <see cref="OperatorHubService"/>
    /// runs in bulk on reconnect: ships a single
    /// <c>OperatorDeployedDeploymentSiteReportDto</c> so the controller can restore
    /// <c>DeploymentState=Deployed</c> for this one deployment site. Used by
    /// <c>DeploymentSiteService.RegisterDeploymentSiteAsync</c> after every successful CR
    /// reconcile — the bulk path captures only deployment sites KubeOps has already
    /// added to <c>DeploymentSiteService._deploymentSites</c> by the time the SignalR connect
    /// callback fires, so any CR that the KubeOps watcher discovers after
    /// that moment would otherwise miss the reverse-sync window.
    ///
    /// Cloud-only: silently no-ops when <c>AutoManageDeploymentSites=false</c> (edge)
    /// or when the connection is down. Best-effort: a failed call is
    /// logged but does not propagate, mirroring the bulk reverse-sync's
    /// self-healing contract.
    /// </summary>
    Task ReportDeployedDeploymentSiteAsync(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Pushes a live progress signal at the controller while a
    /// <c>helm upgrade --install</c> is still in flight. The controller
    /// writes <paramref name="progress"/>.<c>Message</c> onto the workload's
    /// <c>StatusMessage</c> attribute and leaves
    /// <c>DeploymentState</c> at <c>Pending</c>; the terminal outcome
    /// continues to flow through
    /// <c>IOperatorHub.ReportWorkloadDeploymentStatusAsync</c>.
    /// Silently no-ops when the connection is down. Older controllers that
    /// do not implement the method are logged once at warning level; further
    /// calls in that situation degrade silently so the watcher's periodic
    /// pulse does not flood the log.
    /// </summary>
    Task ReportWorkloadDeploymentProgressAsync(WorkloadDeploymentProgressDto progress);
}
