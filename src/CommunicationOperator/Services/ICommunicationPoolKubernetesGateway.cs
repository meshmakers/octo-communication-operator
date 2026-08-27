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
}
