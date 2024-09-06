using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Implements a service for managing diagnostics.
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>
    /// Reconfigure the log level of the service.
    /// </summary>
    /// <param name="minLogLevel">Minimal log level to be logged.</param>
    /// <param name="maxLogLevel">Maximum log level to be logged.</param>
    /// <param name="loggerName">The name of the logger to be reconfigured.</param>
    /// <returns></returns>
    Task ReconfigureLogLevelAsync(LogLevelDto minLogLevel, LogLevelDto maxLogLevel = LogLevelDto.Fatal,
        string loggerName = "*");
}