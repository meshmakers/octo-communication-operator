using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

/// <summary>
/// Admission validator for <see cref="V1CommunicationPoolEntity"/>. Used
/// to also block whitespace in pool names because that whitespace ended
/// up verbatim in derived Kubernetes resource names (Secret, CR
/// metadata.name, labels) and the apiserver rejected those. Since
/// <c>CommunicationPoolManager</c> now sanitises every derived k8s name
/// via <c>K8sNaming.DnsName</c> / <c>K8sNaming.LabelValue</c>, the
/// <c>Spec.PoolName</c> may carry the user-friendly value verbatim
/// (e.g. <c>"Default Cloud"</c>, <c>"Communication Pool"</c>) — the
/// controller uses it as a tenant-scoped lookup key on the SignalR
/// hub, so the original string must be preserved here.
///
/// The only remaining restriction is that the name must not be empty
/// or whitespace-only — that case represents a misconfigured CR and
/// would later break the controller-side dictionary lookup.
/// </summary>
[ValidationWebhook(typeof(V1CommunicationPoolEntity))]
public class CommunicationPoolValidator : ValidationWebhook<V1CommunicationPoolEntity>
{
    public override ValidationResult Create(V1CommunicationPoolEntity newEntity, bool dryRun)
        => string.IsNullOrWhiteSpace(newEntity.Spec.PoolName)
            ? Fail("Pool name must not be empty or whitespace.", StatusCodes.Status400BadRequest)
            : Success();
}
