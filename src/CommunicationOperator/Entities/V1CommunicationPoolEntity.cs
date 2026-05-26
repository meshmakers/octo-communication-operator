using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace Meshmakers.Octo.Communication.Operator.Entities;

[KubernetesEntity(Group = "octo-mesh.meshmakers.io", ApiVersion = "v1alpha1", Kind = "CommunicationPool")]
public class V1CommunicationPoolEntity : CustomKubernetesEntity<V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec, V1CommunicationPoolEntity.V1CommunicationPoolEntityStatus>
{
    public class V1CommunicationPoolEntitySpec
    {
        // The CommunicationPool CR records the operator's INTENT for one
        // pool: "manage pool {PoolRtId} for tenant {TenantId}". PoolRtId
        // is the controller-side runtime entity id and the canonical pool
        // identity — it drives every derived Kubernetes identifier (CR
        // metadata.name, broker secret name, identity labels) because
        // RtIds are 24-char hex strings and always RFC 1123 valid. The
        // human-readable pool display name lives on the controller's
        // RtPool.Name attribute and surfaces in Studio; it is not carried
        // on the CR.
        //
        // Everything else — controller URI, instancePrefix, broker host /
        // port / virtualHost, cert-validation toggles, broker creds
        // secret name — is owned by the operator instance and read from
        // OperatorOptions (OPERATOR__* env vars) at startup. Putting them
        // on the CR was pure duplication; the operator code never read
        // them past PoolDescriptor storage and the duplication invited
        // drift between CR spec and the operator that actually services it.
        public string TenantId { get; set; } = string.Empty;
        public string PoolRtId { get; set; } = string.Empty;
    }

    public class V1CommunicationPoolEntityStatus
    {
        public string CommunicationStatus { get; set; } = string.Empty;
    }
}
