using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class PoolServiceException : Exception
{
    public PoolServiceException()
    {
    }

    public PoolServiceException(string message) : base(message)
    {
    }

    public PoolServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception ConnectionError(string poolRtId, HubException hubException)
    {
        return new PoolServiceException($"Cannot connect to controller for pool rtId {poolRtId}", hubException);
    }

    public static Exception DeployFailed(string poolRtId, Exception exception)
    {
        return new PoolServiceException($"Cannot deploy pool rtId {poolRtId}", exception);
    }

    public static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new PoolServiceException($"[{tenantId}] Failed to pre-update tenant", exception);
    }
}