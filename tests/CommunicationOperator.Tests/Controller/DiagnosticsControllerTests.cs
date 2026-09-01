using System.Net;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Controller;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Controller;

/// <summary>
///     AB#5059 — the operator's diagnostics endpoint reconfigures NLog process-wide and used to be
///     anonymous and cluster-reachable. This host has no authentication scheme at all (see the
///     remarks on <see cref="DiagnosticsController" />), so the gate is a network restriction:
///     the pod itself only, which in practice means <c>kubectl exec</c> / <c>kubectl port-forward</c>
///     and therefore Kubernetes RBAC.
/// </summary>
public class DiagnosticsControllerTests
{
    private readonly IDiagnosticsService _diagnosticsService = Substitute.For<IDiagnosticsService>();

    private DiagnosticsController CreateController(IPAddress? remoteAddress)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = remoteAddress;

        return new DiagnosticsController(NullLogger<DiagnosticsController>.Instance, _diagnosticsService)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Test]
    public async Task ReconfigureLogLevel_FromLoopback_IsAllowed()
    {
        var controller = CreateController(IPAddress.Loopback);

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal);

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await _diagnosticsService.Received(1)
            .ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal, "*");
    }

    [Test]
    public async Task ReconfigureLogLevel_FromIPv6Loopback_IsAllowed()
    {
        var controller = CreateController(IPAddress.IPv6Loopback);

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal);

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await _diagnosticsService.Received(1)
            .ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal, "*");
    }

    /// <summary>
    ///     A dual-stack Kestrel reports an IPv4 loopback client as <c>::ffff:127.0.0.1</c>, which
    ///     <see cref="IPAddress.IsLoopback" /> does not recognise on its own. Getting this wrong would
    ///     lock the endpoint out of the very access path it is being kept for.
    /// </summary>
    [Test]
    public async Task ReconfigureLogLevel_FromIPv4MappedLoopback_IsAllowed()
    {
        var controller = CreateController(IPAddress.Loopback.MapToIPv6());

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal);

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await _diagnosticsService.Received(1)
            .ReconfigureLogLevelAsync(LogLevelDto.Debug, LogLevelDto.Fatal, "*");
    }

    /// <summary>
    ///     The actual hole: any other pod in the cluster reaching the operator's Service.
    /// </summary>
    [Test]
    public async Task ReconfigureLogLevel_FromAnotherPod_IsRefusedAndDoesNotTouchTheLogger()
    {
        var controller = CreateController(IPAddress.Parse("10.244.3.17"));

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Trace, LogLevelDto.Fatal);

        await Assert.That(result).IsTypeOf<ObjectResult>();
        await Assert.That(((ObjectResult)result).StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await _diagnosticsService.DidNotReceiveWithAnyArgs()
            .ReconfigureLogLevelAsync(Arg.Any<LogLevelDto>(), Arg.Any<LogLevelDto>(), Arg.Any<string>());
    }

    [Test]
    public async Task ReconfigureLogLevel_FromPublicAddress_IsRefused()
    {
        var controller = CreateController(IPAddress.Parse("203.0.113.9"));

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Trace, LogLevelDto.Fatal);

        await Assert.That(((ObjectResult)result).StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await _diagnosticsService.DidNotReceiveWithAnyArgs()
            .ReconfigureLogLevelAsync(Arg.Any<LogLevelDto>(), Arg.Any<LogLevelDto>(), Arg.Any<string>());
    }

    /// <summary>
    ///     Fail closed: a transport that exposes no remote endpoint must not be read as "local".
    /// </summary>
    [Test]
    public async Task ReconfigureLogLevel_WithoutRemoteAddress_IsRefused()
    {
        var controller = CreateController(null);

        var result = await controller.ReconfigureLogLevelAsync(LogLevelDto.Trace, LogLevelDto.Fatal);

        await Assert.That(((ObjectResult)result).StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await _diagnosticsService.DidNotReceiveWithAnyArgs()
            .ReconfigureLogLevelAsync(Arg.Any<LogLevelDto>(), Arg.Any<LogLevelDto>(), Arg.Any<string>());
    }

    [Test]
    [Arguments("127.0.0.1", true)]
    [Arguments("127.0.0.53", true)]
    [Arguments("::1", true)]
    [Arguments("10.0.0.1", false)]
    [Arguments("192.168.1.10", false)]
    [Arguments("0.0.0.0", false)]
    [Arguments("fe80::1", false)]
    public async Task IsLoopback_ClassifiesAddresses(string address, bool expected)
    {
        await Assert.That(DiagnosticsController.IsLoopback(IPAddress.Parse(address))).IsEqualTo(expected);
    }
}
