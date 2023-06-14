using KubeOps.Operator.Webhooks;
using PlugOperator.Entities;

namespace PlugOperator.Webhooks;

// public class DemoValidator : IValidationWebhook<V1AgentPoolEntity>
// {
//     public AdmissionOperations Operations => AdmissionOperations.Create;
//
//     public ValidationResult Create(V1AgentPoolEntity newEntity, bool dryRun)
//         => newEntity.Spec.Username == "forbiddenUsername"
//             ? ValidationResult.Fail(StatusCodes.Status400BadRequest, "Username is forbidden")
//             : ValidationResult.Success();
// }
