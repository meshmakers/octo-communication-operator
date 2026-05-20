using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace Meshmakers.Octo.Communication.Operator.Entities;

[KubernetesEntity(Group = "octo-mesh.meshmakers.io", ApiVersion = "v1alpha1", Kind = "CommunicationPool")]
public class V1CommunicationPoolEntity : CustomKubernetesEntity<V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec, V1CommunicationPoolEntity.V1CommunicationPoolEntityStatus>
{
    public class V1CommunicationPoolEntitySpec
    {
        // The CommunicationPool CR records the operator's INTENT for one
        // pool: "manage pool {PoolName} for tenant {TenantId}". Everything
        // else — controller URI, instancePrefix, broker host / port /
        // virtualHost, cert-validation toggles, broker creds secret name —
        // is owned by the operator instance and read from OperatorOptions
        // (OPERATOR__* env vars) at startup. Putting them on the CR was
        // pure duplication; the operator code never read them past
        // PoolDescriptor storage and the duplication invited drift between
        // CR spec and the operator that actually services it.
        public string TenantId { get; set; } = string.Empty;
        public string PoolName { get; set; } = string.Empty;
    }

    public class V1CommunicationPoolEntityStatus
    {
        public string CommunicationStatus { get; set; } = string.Empty;
    }
}
