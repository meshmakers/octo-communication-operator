using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace Meshmakers.Octo.Communication.Operator.Entities;

[KubernetesEntity(Group = "octo-mesh.meshmakers.io", ApiVersion = "v1alpha1", Kind = "CommunicationPool")]
public class V1CommunicationPoolEntity : CustomKubernetesEntity<V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec, V1CommunicationPoolEntity.V1CommunicationPoolEntityStatus>
{
    public class V1CommunicationPoolEntitySpec
    {
        public string TenantId { get; set; } = string.Empty;
        public string PoolName { get; set; } = string.Empty;
        public string CommunicationControllerUri { get; set; } = string.Empty;

        public string InstancePrefix { get; set; } = string.Empty;

        public bool IgnoreCertificateValidation { get; set; }

        public string BrokerHost { get; set; } = string.Empty;
        
        public string BrokerVirtualHost { get; set; } = string.Empty;
        
        public int BrokerPort { get; set; } = 5672;
        
        public string BrokerUserNameSecret { get; set; } = string.Empty;
    }

    public class V1CommunicationPoolEntityStatus
    {
        public string CommunicationStatus { get; set; } = string.Empty;
    }
}
