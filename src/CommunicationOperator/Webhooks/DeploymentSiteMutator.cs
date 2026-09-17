using KubeOps.Operator.Web.Webhooks.Admission.Mutation;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

[MutationWebhook(typeof(V1DeploymentSiteEntity))]
public class DeploymentSiteMutator : MutationWebhook<V1DeploymentSiteEntity>
{
    public override MutationResult<V1DeploymentSiteEntity> Create(V1DeploymentSiteEntity newEntity, bool dryRun)
    {
        return NoChanges();
    }
}
