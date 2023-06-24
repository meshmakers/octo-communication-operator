using KubeOps.Operator.Webhooks;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

public class CommunicationPoolValidator : IValidationWebhook<V1CommunicationPoolEntity>
{
    public AdmissionOperations Operations => AdmissionOperations.Create;

    public ValidationResult Create(V1CommunicationPoolEntity newEntity, bool dryRun)
        => newEntity.Spec.PoolName.Contains(" ")
            ? ValidationResult.Fail(StatusCodes.Status400BadRequest, "Pool name is not allowed to have spaces.")
            : ValidationResult.Success();
}
