using Meshmakers.Octo.Common.Shared;

namespace PlugOperator.Reconcilers;

public class PlugReconsilerException : Exception
{
    public PlugReconsilerException()
    {
    }

    public PlugReconsilerException(string message) : base(message)
    {
    }

    public PlugReconsilerException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception PlugReconcileFailed(OctoObjectId plugRtId, Exception exception)
    {
        return new PlugReconsilerException($"Plug reconcile failed for plug {plugRtId}", exception);
    }

    internal static Exception PoolDeleteFailed(string k8PoolPoolName, Exception exception)
    {
        return new PlugReconsilerException($"Plug delete failed for pool {k8PoolPoolName}", exception);
    }

    internal static Exception PlugDeleteFailed(OctoObjectId plugRtId, Exception exception)
    {
        return new PlugReconsilerException($"Plug delete failed for plug {plugRtId}", exception);
    }
}
