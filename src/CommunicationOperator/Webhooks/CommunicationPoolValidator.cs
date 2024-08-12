using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

[ValidationWebhook(typeof(V1CommunicationPoolEntity))]
public class CommunicationPoolValidator : ValidationWebhook<V1CommunicationPoolEntity>
{
    public override ValidationResult Create(V1CommunicationPoolEntity newEntity, bool dryRun)
        => newEntity.Spec.PoolName.Contains(" ")
            ? Fail("Pool name is not allowed to have spaces.", StatusCodes.Status400BadRequest)
            : Success();
}