using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

public class AdapterReconsilerException : Exception
{
    public AdapterReconsilerException()
    {
    }

    public AdapterReconsilerException(string message) : base(message)
    {
    }

    public AdapterReconsilerException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception AdapterReconcileFailed(RtEntityId adapterRtEntityId, Exception exception)
    {
        return new AdapterReconsilerException($"Reconcile failed for adapter {adapterRtEntityId}", exception);
    }

    internal static Exception PoolDeleteFailed(string k8PoolPoolName, Exception exception)
    {
        return new AdapterReconsilerException($"Delete failed for pool {k8PoolPoolName}", exception);
    }

    internal static Exception AdapterDeleteFailed(RtEntityId adapterRtEntityId, Exception exception)
    {
        return new AdapterReconsilerException($"Delete failed for adapter {adapterRtEntityId}", exception);
    }
}
