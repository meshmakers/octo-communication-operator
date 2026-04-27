namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Manages CommunicationPool custom resources and broker secrets in response to tenant lifecycle events.
/// </summary>
public interface ICommunicationPoolManager
{
    /// <summary>
    /// Creates a CommunicationPool CR and the associated broker credentials secret for the given tenant.
    /// If the CR already exists, no action is taken (idempotent).
    /// </summary>
    Task CreateCommunicationPoolAsync(string tenantId);

    /// <summary>
    /// Deletes the CommunicationPool CR and the associated broker credentials secret for the given tenant.
    /// If the CR does not exist, no action is taken (idempotent).
    /// </summary>
    Task DeleteCommunicationPoolAsync(string tenantId);
}
