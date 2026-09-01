using System.ComponentModel.DataAnnotations;
using System.Net;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Communication.Operator.Controller;

/// <summary>
/// Manages the diagnostics settings of the service
/// </summary>
/// <remarks>
/// 🔴 AB#5059 — this endpoint used to be reachable by anything that could open a TCP connection to
/// the operator pod. It reconfigures NLog process-wide, so any workload in the cluster could turn
/// every logger to Trace (a disk / log-pipeline denial-of-service, and a disclosure lever on a
/// process that handles broker credentials, cluster secrets and helm values) or silence them all
/// while doing something else.
/// <para>
/// <b>Why the fix is a network restriction and not <c>[Authorize]</c>.</b> This host is a KubeOps
/// operator: <c>Program.cs</c> registers <c>AddKubernetesOperator()</c>, <c>AddHealthChecks()</c>
/// and <c>AddControllers()</c> and nothing else — there is no authentication scheme, no
/// authorization service, no token authority and no JWT configuration anywhere in the repository.
/// An <c>[Authorize]</c> attribute here would not gate the endpoint, it would make every request to
/// it fail with <c>InvalidOperationException: No authenticationScheme was specified, and there was
/// no DefaultChallengeScheme found</c>. Introducing an authority, an OIDC client and a bearer
/// scheme purely for this one endpoint is the "größerer Umbau" that AB#5059 explicitly allows us to
/// decline, and it would also have to answer which identity a cluster-internal operator should even
/// present.
/// </para>
/// <para>
/// <b>Why not simply delete it.</b> Nothing in the checkout calls it — <c>octo-cli</c>'s
/// <c>ReconfigureLogLevel</c> dispatches to identity / asset-repo / bot / communication-controller /
/// reporting, there is no operator service client in <c>octo-sdk</c>, and the MCP
/// <c>reconfigure_log_level</c> tool goes to bot services. So deleting it would break no caller. It
/// is kept because raising the operator's log level without restarting the pod is genuinely useful
/// while a helm rollout is misbehaving, and a restart loses exactly the state one wants to observe.
/// </para>
/// <para>
/// <b>What loopback buys.</b> The remaining reachable paths are <c>kubectl exec … curl</c> and
/// <c>kubectl port-forward</c> — the port-forward stream is proxied by the kubelet <i>into</i> the
/// pod's network namespace, so it arrives from <c>127.0.0.1</c>. Both require Kubernetes RBAC on
/// the pod, which is the authorization this process cannot perform itself but the cluster already
/// does. Every other pod, every Service route and every ingress path is refused. A missing remote
/// address is treated as non-loopback (fail closed).
/// </para>
/// <para>
/// The route stays <c>system/v1/[controller]</c>. This host has no API-versioning services
/// (<c>AddOctoApiVersioningAndDocumentation</c> is a platform-service concern), so the literal
/// <c>v1</c> in the template is the version pin; adding <c>[ApiVersion]</c> here would require
/// wiring <c>Asp.Versioning</c> into an operator that publishes no API surface.
/// </para>
/// </remarks>
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
    public async Task<IActionResult> ReconfigureLogLevelAsync([Required] LogLevelDto minLogLevel,
        [Required] LogLevelDto maxLogLevel, string loggerName = "*")
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress;
        if (!IsLoopback(remoteAddress))
        {
            _logger.LogWarning(
                "Refused a remote request to reconfigure log levels from {RemoteAddress}. This endpoint is restricted to the pod itself (kubectl exec / port-forward)",
                remoteAddress?.ToString() ?? "<unknown>");
            return StatusCode(StatusCodes.Status403Forbidden,
                "The diagnostics endpoint of the communication operator is reachable from the pod itself only. Use 'kubectl port-forward' or 'kubectl exec'.");
        }

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

    /// <summary>
    /// True when the request originates from the pod itself.
    /// </summary>
    /// <remarks>
    /// A null address (no remote endpoint, e.g. a transport that does not expose one) is NOT
    /// loopback — the check fails closed. IPv4-mapped IPv6 addresses (<c>::ffff:127.0.0.1</c>, what
    /// a dual-stack Kestrel reports for an IPv4 loopback client) are unmapped first, because
    /// <see cref="IPAddress.IsLoopback" /> does not recognise them.
    /// </remarks>
    internal static bool IsLoopback(IPAddress? remoteAddress)
    {
        if (remoteAddress == null)
        {
            return false;
        }

        var address = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        return IPAddress.IsLoopback(address);
    }
}
