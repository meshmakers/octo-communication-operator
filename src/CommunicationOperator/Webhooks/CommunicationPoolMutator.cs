using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

[MutationWebhook(typeof(V1CommunicationPoolEntity))]
public class CommunicationPoolMutator : MutationWebhook<V1CommunicationPoolEntity>
{
    public override MutationResult<V1CommunicationPoolEntity> Create(V1CommunicationPoolEntity newEntity, bool dryRun)
    {
        return NoChanges();
    }
}
