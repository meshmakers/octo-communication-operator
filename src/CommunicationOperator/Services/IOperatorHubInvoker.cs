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
}
