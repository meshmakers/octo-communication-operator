using k8s;
using k8s.Models;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global

namespace Meshmakers.Octo.Communication.Operator.Models;

/// <summary>
/// Represents a Kubernetes Deployment.
/// </summary>
/// <remarks>
/// This class was added because the KubernetesClient library throws an exception during deserialization of the
/// status property of the original <see cref="V1Deployment"/> class.
/// </remarks>
[KubernetesEntity(Group=KubeGroup, Kind=KubeKind, ApiVersion=KubeApiVersion, PluralName=KubePluralName)]
public class V1DeploymentTemporary : IKubernetesObject<V1ObjectMeta>, ISpec<V1DeploymentSpec>, IValidate
{
    public const string KubeApiVersion = "v1";
    public const string KubeKind = "Deployment";
    public const string KubeGroup = "apps";
    public const string KubePluralName = "deployments";
    public string ApiVersion { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public V1ObjectMeta Metadata { get; set; } = null!;
    public V1DeploymentSpec Spec { get; set; } = null!;
    public void Validate()
    {
    }
}