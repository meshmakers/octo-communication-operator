using KubeOps.Operator.Webhooks;
using PlugOperator.Entities;

namespace PlugOperator.Webhooks;

public class DemoMutator : IMutationWebhook<V1PlugPoolEntity>
{
    public AdmissionOperations Operations => AdmissionOperations.Create;

    public MutationResult Create(V1PlugPoolEntity newEntity, bool dryRun)
    {
        newEntity.Spec.PlugPoolName = "not foobar";
        return MutationResult.Modified(newEntity);
    }
}
