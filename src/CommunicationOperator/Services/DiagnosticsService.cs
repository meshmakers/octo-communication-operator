using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NLog;
using NLog.Config;
using LogLevel = NLog.LogLevel;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Implements a service for managing diagnostics.
/// </summary>
public class DiagnosticsService : IDiagnosticsService
{
    public Task ReconfigureLogLevelAsync(LogLevelDto minLogLevel, LogLevelDto maxLogLevel = LogLevelDto.Fatal,
        string loggerName = "Meshmakers.*")
    {
        var minLevel = LogLevel.FromOrdinal((int)minLogLevel);
        var maxLevel = LogLevel.FromOrdinal((int)maxLogLevel);

        if (LogManager.Configuration == null)
        {
            throw new InvalidOperationException("NLog configuration is not initialized.");
        }

        var loggingRule = LogManager.Configuration.LoggingRules.SingleOrDefault(r => r.LoggerNamePattern == loggerName);
        
        // If the logger is disabled, remove the logging rule
        if (minLevel == LogLevel.Off && maxLevel == LogLevel.Off && loggerName != "*")
        {
            if (loggingRule != null)
            {
                LogManager.Configuration.LoggingRules.Remove(loggingRule);
                LogManager.ReconfigExistingLoggers();
            }
            return Task.CompletedTask;
        }
        
        // Create a new logging rule if it does not exist
        if (loggingRule == null)
        {
            var target = LogManager.Configuration.FindTargetByName("coloredConsole");
            if (target == null)
            {
                throw new InvalidOperationException("Target 'coloredConsole' not found in NLog configuration.");
            }
            loggingRule = new LoggingRule(loggerName, target);
            LogManager.Configuration.LoggingRules.Insert(0, loggingRule);
        }
        
        // Configure the logging rule
        loggingRule.DisableLoggingForLevels(LogLevel.Trace, LogLevel.Fatal);
        loggingRule.EnableLoggingForLevels(minLevel, maxLevel);

        LogManager.ReconfigExistingLoggers();

        return Task.CompletedTask;
    }
}