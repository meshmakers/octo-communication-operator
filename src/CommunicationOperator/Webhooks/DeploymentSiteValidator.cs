using System.Text.RegularExpressions;
using KubeOps.Operator.Web.Webhooks.Admission.Validation;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Webhooks;

/// <summary>
/// Admission validator for <see cref="V1DeploymentSiteEntity"/>.
///
/// The only required field is <c>Spec.DeploymentSiteRtId</c>: it must be a
/// 24-character lowercase hex MongoDB ObjectId. The controller's
/// <c>OperatorHub.RegisterDeploymentSiteAsync</c> parses this with
/// <c>OctoObjectId.TryParse</c>; an empty or malformed value used to
/// surface as a hub-side <c>FormatException</c> ("'' is not a valid 24
/// digit hex string") and the operator got stuck retrying the same
/// broken CR forever. Rejecting at admission means the misconfigured
/// Ansible / kubectl apply fails loudly at write time, with the bad
/// value named in the apiserver response.
///
/// <c>Spec.DeploymentSiteName</c> is optional — the canonical deployment site identity is
/// the rtId, and the human-readable display name lives on the
/// controller's <c>RtDeploymentSite.Name</c> entity, not on the CR.
///
/// We deliberately do not validate <c>Spec.TenantId</c> here because
/// the tenant id is just a routing key on the wire — the controller
/// will reject an unknown tenant with its own typed exception that
/// already surfaces clearly.
/// </summary>
[ValidationWebhook(typeof(V1DeploymentSiteEntity))]
public class DeploymentSiteValidator : ValidationWebhook<V1DeploymentSiteEntity>
{
    private static readonly Regex DeploymentSiteRtIdPattern = new("^[a-f0-9]{24}$", RegexOptions.Compiled);

    public override ValidationResult Create(V1DeploymentSiteEntity newEntity, bool dryRun)
        => Validate(newEntity);

    public override ValidationResult Update(V1DeploymentSiteEntity oldEntity, V1DeploymentSiteEntity newEntity, bool dryRun)
        => Validate(newEntity);

    private ValidationResult Validate(V1DeploymentSiteEntity entity)
    {
        if (string.IsNullOrEmpty(entity.Spec.DeploymentSiteRtId))
        {
            return Fail(
                "DeploymentSite runtime id (spec.deploymentSiteRtId) must be set to the 24-character hex MongoDB ObjectId " +
                "of the controller-side RtDeploymentSite entity.",
                StatusCodes.Status400BadRequest);
        }

        if (!DeploymentSiteRtIdPattern.IsMatch(entity.Spec.DeploymentSiteRtId))
        {
            return Fail(
                $"DeploymentSite runtime id (spec.deploymentSiteRtId) '{entity.Spec.DeploymentSiteRtId}' is not a valid 24-character " +
                "lowercase hex MongoDB ObjectId.",
                StatusCodes.Status400BadRequest);
        }

        return Success();
    }
}
