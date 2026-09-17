using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace Meshmakers.Octo.Communication.Operator.Entities;

[KubernetesEntity(Group = "octo-mesh.meshmakers.io", ApiVersion = "v1", Kind = "DeploymentSite")]
public class V1DeploymentSiteEntity : CustomKubernetesEntity<V1DeploymentSiteEntity.V1DeploymentSiteEntitySpec, V1DeploymentSiteEntity.V1DeploymentSiteEntityStatus>
{
    public class V1DeploymentSiteEntitySpec
    {
        // The DeploymentSite CR records the operator's INTENT for one
        // deployment site: "manage deployment site {DeploymentSiteRtId} for tenant {TenantId}". DeploymentSiteRtId
        // is the controller-side runtime entity id and the canonical deployment site
        // identity — it drives every derived Kubernetes identifier (CR
        // metadata.name, broker secret name, identity labels) because
        // RtIds are 24-char hex strings and always RFC 1123 valid. The
        // human-readable deployment site display name lives on the controller's
        // RtDeploymentSite.Name attribute and surfaces in Studio; it is not carried
        // on the CR.
        //
        // Everything else — controller URI, instancePrefix, broker host /
        // port / virtualHost, cert-validation toggles, broker creds
        // secret name — is owned by the operator instance and read from
        // OperatorOptions (OPERATOR__* env vars) at startup. Putting them
        // on the CR was pure duplication; the operator code never read
        // them past DeploymentSiteDescriptor storage and the duplication invited
        // drift between CR spec and the operator that actually services it.
        public string TenantId { get; set; } = string.Empty;
        public string DeploymentSiteRtId { get; set; } = string.Empty;
    }

    public class V1DeploymentSiteEntityStatus
    {
        public string CommunicationStatus { get; set; } = string.Empty;
    }
}
