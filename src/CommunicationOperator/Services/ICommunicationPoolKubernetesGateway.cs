using k8s.Models;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Thin abstraction over <see cref="k8s.IKubernetes"/> for the resources the
/// <see cref="CommunicationPoolManager"/> touches: CommunicationPool custom
/// resources and the per-tenant broker credentials Secret. Encapsulates the
/// 404-via-<c>HttpOperationException</c> idiom of the Kubernetes client so
/// that the manager can be tested without mocking the k8s SDK directly.
/// </summary>
public interface ICommunicationPoolKubernetesGateway
{
    Task<bool> CommunicationPoolExistsAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    Task CreateCommunicationPoolAsync(string @namespace, object resource, CancellationToken cancellationToken = default);

    Task DeleteCommunicationPoolAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    Task<bool> SecretExistsAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    Task CreateSecretAsync(string @namespace, V1Secret secret, CancellationToken cancellationToken = default);

    Task DeleteSecretAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the creation timestamp of the secret, or <c>null</c> when it does not exist.
    /// Used by the helm stale-lock recovery (AB#4894) to judge whether a <c>pending-*</c>
    /// release secret belongs to a live helm run or to one killed mid-upgrade.
    /// </summary>
    Task<DateTime?> GetSecretCreationTimestampAsync(string @namespace, string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches <c>spec.replicas</c> on every Deployment carrying the
    /// <c>app.kubernetes.io/instance={instance}</c> label (AB#4917, on-demand lifecycle).
    /// The label lookup is deliberate — Application charts may render resource names as
    /// <c>{release}-{chart}</c>, so deriving the Deployment name from the release is unsafe.
    /// A plain merge patch on the Deployment (not the scale subresource) so the call runs
    /// under the operator's existing <c>apps/deployments: ['*']</c> RBAC.
    /// Returns the number of Deployments patched (0 when the release has none).
    /// </summary>
    Task<int> ScaleDeploymentsByInstanceAsync(string @namespace, string instance, int replicas,
        CancellationToken cancellationToken = default);
}
