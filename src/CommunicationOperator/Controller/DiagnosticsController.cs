using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Communication.Operator.Controller;

/// <summary>
/// Manages the diagnostics settings of the service
/// </summary>
[ApiController]
[Route("system/v1/[controller]")]
public class DiagnosticsController: ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly IDiagnosticsService _diagnosticsService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="diagnosticsService"></param>
    public DiagnosticsController(ILogger<DiagnosticsController> logger, IDiagnosticsService diagnosticsService)
    {
        _logger = logger;
        _diagnosticsService = diagnosticsService;
    }

    /// <summary>
    /// Reconfigures the log level of the service
    /// </summary>
    /// <param name="minLogLevel">The minimal log level to be logged.</param>
    /// <param name="maxLogLevel">The maximal log level to be logged.</param>
    /// <param name="loggerName">The name of the logger to be reconfigured.</param>
    /// <returns></returns>
    [HttpPost("reconfigureLogLevel")]
  //  [Authorize(AssetRepositoryServiceConstants.SystemApiReadWritePolicy)]
    public async Task<IActionResult> ReconfigureLogLevelAsync([Required] LogLevelDto minLogLevel,
        [Required] LogLevelDto maxLogLevel, string loggerName = "*")
    {
        try
        {
            _logger.LogInformation("Reconfiguring logger {LoggerName} log level to min level {MinLogLevel}, max level {MaxLoglevel}", loggerName, minLogLevel, maxLogLevel);
            await _diagnosticsService.ReconfigureLogLevelAsync(minLogLevel, maxLogLevel, loggerName);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}