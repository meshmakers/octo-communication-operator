namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Manages CommunicationPool custom resources and broker secrets in response
/// to Cloud pool deploy / undeploy events.
/// </summary>
public interface ICommunicationPoolManager
{
    /// <summary>
    /// Creates a CommunicationPool CR and the associated broker credentials
    /// secret for the given tenant + pool. If the CR already exists, no
    /// action is taken (idempotent).
    /// </summary>
    Task CreatePoolAsync(string tenantId, string poolName);

    /// <summary>
    /// Deletes the CommunicationPool CR and the associated broker credentials
    /// secret for the given tenant + pool. If the CR does not exist, no
    /// action is taken (idempotent).
    /// </summary>
    Task DeletePoolAsync(string tenantId, string poolName);
}
