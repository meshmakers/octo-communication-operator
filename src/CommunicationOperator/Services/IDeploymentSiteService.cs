using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;

namespace Meshmakers.Octo.Communication.Operator.Services;

public interface IDeploymentSiteService
{
    Task RegisterDeploymentSiteAsync(V1DeploymentSiteEntity entity, CancellationToken cancellationToken);
    Task UnRegisterDeploymentSiteAsync(V1DeploymentSiteEntity entity);

    /// <summary>
    /// Snapshot of every deployment site the operator currently owns. The operator-hub
    /// reconnect handler reads this to know which deployment sites to re-register over
    /// the freshly-established connection.
    /// </summary>
    IReadOnlyCollection<DeploymentSite> GetDeploymentSites();

    /// <summary>
    /// Resets <see cref="DeploymentSite.IsRegistered"/> on every owned deployment site to
    /// <c>false</c>. Called by the operator-hub service when its SignalR
    /// connection drops so the next reconnect cycle replays every
    /// registration.
    /// </summary>
    void ResetRegistrationState();
}
