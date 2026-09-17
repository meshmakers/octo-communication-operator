using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class DeploymentSiteServiceException : Exception
{
    public DeploymentSiteServiceException()
    {
    }

    public DeploymentSiteServiceException(string message) : base(message)
    {
    }

    public DeploymentSiteServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception ConnectionError(string deploymentSiteRtId, HubException hubException)
    {
        return new DeploymentSiteServiceException($"Cannot connect to controller for deployment site rtId {deploymentSiteRtId}", hubException);
    }

    public static Exception DeployFailed(string deploymentSiteRtId, Exception exception)
    {
        return new DeploymentSiteServiceException($"Cannot deploy deployment site rtId {deploymentSiteRtId}", exception);
    }

    public static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Failed to pre-update tenant", exception);
    }
}