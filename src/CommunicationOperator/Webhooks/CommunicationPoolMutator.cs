using KubeOps.Operator.Webhooks;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

public class CommunicationPoolMutator : IMutationWebhook<V1CommunicationPoolEntity>
{
    public AdmissionOperations Operations => AdmissionOperations.Create;

    public MutationResult Create(V1CommunicationPoolEntity newEntity, bool dryRun)
    {
   //     newEntity.Spec.PoolName = "not foobar";
        return MutationResult.NoChanges();
    }
}
