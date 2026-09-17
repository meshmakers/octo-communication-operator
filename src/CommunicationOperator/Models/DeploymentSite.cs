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
}
