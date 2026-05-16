using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;

namespace Meshmakers.Octo.Communication.Operator.Services;

public interface IPoolService
{
    Task RegisterPoolAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken);
    Task UnRegisterPoolAsync(V1CommunicationPoolEntity entity);

    /// <summary>
    /// Snapshot of every pool the operator currently owns. The operator-hub
    /// reconnect handler reads this to know which pools to re-register over
    /// the freshly-established connection.
    /// </summary>
    IReadOnlyCollection<Pool> GetPools();

    /// <summary>
    /// Resets <see cref="Pool.IsRegistered"/> on every owned pool to
    /// <c>false</c>. Called by the operator-hub service when its SignalR
    /// connection drops so the next reconnect cycle replays every
    /// registration.
    /// </summary>
    void ResetRegistrationState();
}
