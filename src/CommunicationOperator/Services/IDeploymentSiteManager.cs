namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Manages DeploymentSite custom resources and broker secrets in response
/// to Cloud deployment site deploy / undeploy events. The lookup key on the Kubernetes
/// side is the deployment site's runtime entity id (deploymentSiteRtId) — 24-char lowercase hex,
/// always RFC 1123 valid — so every derived resource name survives controller-
/// side renames of the user-facing deployment site name.
/// </summary>
public interface IDeploymentSiteManager
{
    /// <summary>
    /// Creates a DeploymentSite CR and the associated broker credentials
    /// secret for the given tenant + deployment site. If the CR already exists, no
    /// action is taken (idempotent).
    /// </summary>
    Task CreateDeploymentSiteAsync(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Deletes the DeploymentSite CR and the associated broker credentials
    /// secret for the given tenant + deploymentSiteRtId. If the CR does not exist, no
    /// action is taken (idempotent).
    /// </summary>
    Task DeleteDeploymentSiteAsync(string tenantId, string deploymentSiteRtId);
}
