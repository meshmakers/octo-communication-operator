using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Models;

/// <summary>
/// In-memory record of a <c>DeploymentSite</c> CR the operator currently
/// owns. Used to remember which deployment sites to (re-)register on the operator hub
/// after a reconnect, and which to release when the CR is deleted.
/// </summary>
public class DeploymentSite(K8DeploymentSite descriptor, V1DeploymentSiteEntity entity)
{
    public K8DeploymentSite Descriptor { get; } = descriptor;
    public V1DeploymentSiteEntity Entity { get; } = entity;

    /// <summary>
    /// True once the operator has invoked
    /// <see cref="Meshmakers.Octo.Communication.Contracts.Hubs.IOperatorHub.RegisterDeploymentSiteAsync"/>
    /// on the controller for this deployment site. Reset to <c>false</c> on connection
    /// drops so the reconnect handler knows to re-register.
    /// </summary>
    public bool IsRegistered { get; set; }

    /// <summary>
    /// Number of consecutive registration attempts of the periodic retry loop that the
    /// controller rejected. Drives the exponential back-off (AB#8160); reset on success
    /// and on a connection drop.
    /// </summary>
    public int ConsecutiveRegistrationFailures { get; set; }

    /// <summary>
    /// Earliest time the periodic retry loop attempts the next registration of this
    /// deployment site. <see cref="DateTimeOffset.MinValue"/> means "on the next tick".
    /// </summary>
    public DateTimeOffset NextRegistrationAttempt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Clears the back-off state so the next retry tick attempts the registration again.
    /// </summary>
    public void ResetRegistrationBackoff()
    {
        ConsecutiveRegistrationFailures = 0;
        NextRegistrationAttempt = DateTimeOffset.MinValue;
    }
}
