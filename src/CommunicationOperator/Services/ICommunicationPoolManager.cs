namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Manages CommunicationPool custom resources and broker secrets in response
/// to Cloud pool deploy / undeploy events. The lookup key on the Kubernetes
/// side is the pool's runtime entity id (poolRtId) — 24-char lowercase hex,
/// always RFC 1123 valid — so every derived resource name survives renames
/// of the user-facing PoolName.
/// </summary>
public interface ICommunicationPoolManager
{
    /// <summary>
    /// Creates a CommunicationPool CR and the associated broker credentials
    /// secret for the given tenant + pool. If the CR already exists, no
    /// action is taken (idempotent). <paramref name="poolName"/> is the
    /// user-facing display name, stored on the resources only as an
    /// annotation.
    /// </summary>
    Task CreatePoolAsync(string tenantId, string poolRtId, string poolName);

    /// <summary>
    /// Deletes the CommunicationPool CR and the associated broker credentials
    /// secret for the given tenant + poolRtId. If the CR does not exist, no
    /// action is taken (idempotent).
    /// </summary>
    Task DeletePoolAsync(string tenantId, string poolRtId, string poolName);
}
