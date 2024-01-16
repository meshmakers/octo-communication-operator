using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

public class CommunicationAdapterReconsilerException : Exception
{
    public CommunicationAdapterReconsilerException()
    {
    }

    public CommunicationAdapterReconsilerException(string message) : base(message)
    {
    }

    public CommunicationAdapterReconsilerException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception AdapterReconcileFailed(CkId<CkTypeId> adapterCkId, OctoObjectId adapterRtId, Exception exception)
    {
        return new CommunicationAdapterReconsilerException($"Reconcile failed for adapter {adapterCkId}/{adapterRtId}", exception);
    }

    internal static Exception PoolDeleteFailed(string k8PoolPoolName, Exception exception)
    {
        return new CommunicationAdapterReconsilerException($"Delete failed for pool {k8PoolPoolName}", exception);
    }

    internal static Exception AdapterDeleteFailed(CkId<CkTypeId> adapterCkId, OctoObjectId adapterRtId, Exception exception)
    {
        return new CommunicationAdapterReconsilerException($"Delete failed for adapter {adapterCkId}/{adapterRtId}", exception);
    }
}
