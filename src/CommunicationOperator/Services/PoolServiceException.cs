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

    public static Exception ConnectionError(string poolName, HubException hubException)
    {
        return new PoolServiceException($"Cannot connect to controller {poolName}", hubException); 
    }

    public static Exception DeployFailed(string poolName, Exception exception)
    {       
        return new PoolServiceException($"Cannot deploy pool {poolName}", exception);
    }
}