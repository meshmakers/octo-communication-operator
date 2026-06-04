using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Thin client-side wrapper around the single <c>/operatorHub</c> connection.
/// Exists so non-hosted services (currently <see cref="PoolService"/>) can
/// invoke hub methods without taking a dependency on
/// <see cref="OperatorHubService"/> — the latter holds the connection's
/// lifecycle.
///
/// All methods are no-ops when <see cref="IsConnected"/> is <c>false</c>;
/// the operator's reconnect handler reads the local pool list and replays
/// any missed Register calls once the connection comes back.
/// </summary>
public interface IOperatorHubInvoker
{
    /// <summary>
    /// <c>true</c> while the operator-hub SignalR connection is alive.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Invokes <c>IOperatorHub.RegisterPoolAsync</c> on the controller for
    /// the given pool. Silently skips when the connection is down.
    /// <paramref name="poolRtId"/> is the controller-side lookup key.
    /// </summary>
    Task RegisterPoolAsync(string tenantId, string poolRtId);

    /// <summary>
    /// Invokes <c>IOperatorHub.UnregisterPoolAsync</c>. Silently skips when
    /// the connection is down — the pool will be reset to <c>Offline</c>
    /// the next time the operator-hub disconnects or when the next
    /// reconnect runs without that pool in the local list.
    /// </summary>
    Task UnregisterPoolAsync(string tenantId, string poolRtId);

    /// <summary>
    /// Per-pool variant of the reverse-sync that <see cref="OperatorHubService"/>
    /// runs in bulk on reconnect: ships a single
    /// <c>OperatorDeployedPoolReportDto</c> so the controller can restore
    /// <c>DeploymentState=Deployed</c> for this one pool. Used by
    /// <c>PoolService.RegisterPoolAsync</c> after every successful CR
    /// reconcile — the bulk path captures only pools KubeOps has already
    /// added to <c>PoolService._pools</c> by the time the SignalR connect
    /// callback fires, so any CR that the KubeOps watcher discovers after
    /// that moment would otherwise miss the reverse-sync window.
    ///
    /// Cloud-only: silently no-ops when <c>AutoManagePools=false</c> (edge)
    /// or when the connection is down. Best-effort: a failed call is
    /// logged but does not propagate, mirroring the bulk reverse-sync's
    /// self-healing contract.
    /// </summary>
    Task ReportDeployedPoolAsync(string tenantId, string poolRtId);

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
