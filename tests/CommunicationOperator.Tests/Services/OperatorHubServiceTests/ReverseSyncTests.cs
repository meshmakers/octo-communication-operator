using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

/// <summary>
/// Pins the post-(re)connect reverse-sync handshake: a Cloud operator with
/// active DeploymentSite CRs reports them via <c>ReportDeployedStateAsync</c>
/// so the controller can restore <c>DeploymentState=Deployed</c> on any deployment site
/// whose state drifted while the operator was offline. Edge operators must
/// NOT call it — the hub contract rejects them.
/// </summary>
public class ReverseSyncTests : OperatorHubServiceTestsBase
{
    private static DeploymentSite MakeDeploymentSite(string tenantId, string deploymentSiteRtId)
    {
        var entity = new V1DeploymentSiteEntity
        {
            Spec = new V1DeploymentSiteEntity.V1DeploymentSiteEntitySpec
            {
                TenantId = tenantId,
                DeploymentSiteRtId = deploymentSiteRtId,
            },
        };
        return new DeploymentSite(new K8DeploymentSite { TenantId = tenantId, DeploymentSiteRtId = deploymentSiteRtId, Namespace = "octo" }, entity);
    }

    [Test]
    public async Task CloudMode_WithOwnedDeploymentSites_SendsReverseSync()
    {
        // Smoking-gun fix path: operator was restarted, CRs survived in k8s,
        // controller may have lost track of the deployment sites' deployed state. The
        // reverse-sync hands the controller the list to restore from.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        DeploymentSiteService.GetDeploymentSites().Returns(new[]
        {
            MakeDeploymentSite("tenant-a", "65d5c447b420da3fb12381a1"),
            MakeDeploymentSite("tenant-b", "65d5c447b420da3fb12381a2"),
        });

        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).ReportDeployedStateAsync(
            Arg.Is<IReadOnlyList<OperatorDeployedDeploymentSiteReportDto>>(reports =>
                reports.Count == 2
                && reports.Any(r => r.TenantId == "tenant-a" && r.DeploymentSiteRtId == "65d5c447b420da3fb12381a1")
                && reports.Any(r => r.TenantId == "tenant-b" && r.DeploymentSiteRtId == "65d5c447b420da3fb12381a2")));

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task EdgeMode_DoesNotCallReportDeployedState()
    {
        // The hub contract rejects edge operators with a HubException — we
        // must not even attempt the call from the edge side, otherwise every
        // reconnect would emit an avoidable error audit event on the
        // controller.
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        DeploymentSiteService.GetDeploymentSites().Returns(new[] { MakeDeploymentSite("tenant-a", "65d5c447b420da3fb12381a1") });

        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.DidNotReceiveWithAnyArgs().ReportDeployedStateAsync(default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task CloudMode_NoOwnedDeploymentSites_DoesNotCallReportDeployedState()
    {
        // Fresh install: CR list is empty, nothing to report. Skip the call
        // entirely — sending an empty list is a valid no-op on the controller
        // but adds round-trip cost and log noise on every reconnect for
        // no benefit.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        DeploymentSiteService.GetDeploymentSites().Returns(Array.Empty<DeploymentSite>());

        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.DidNotReceiveWithAnyArgs().ReportDeployedStateAsync(default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReportDeployedStateAsync_ThrowsHubException_IsLoggedAndDoesNotPropagate()
    {
        // Self-healing is best-effort. If the controller is on an older build
        // that doesn't recognise ReportDeployedStateAsync (or rejects it for
        // any reason), the operator must keep running and let the next
        // deploy/undeploy event write the state — failing the reconnect
        // because the reverse-sync threw would leave us in an even worse
        // place (no connection, no future event reception).
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        DeploymentSiteService.GetDeploymentSites().Returns(new[] { MakeDeploymentSite("tenant-a", "65d5c447b420da3fb12381a1") });

        var setup = SetupClient();
        setup.Client
            .ReportDeployedStateAsync(Arg.Any<IReadOnlyList<OperatorDeployedDeploymentSiteReportDto>>())
            .ThrowsAsync(new InvalidOperationException("controller-rejected"));

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        // Reverse-sync was attempted but failure didn't crash the service.
        // We're past EnableReconnect (which is called AFTER the connect
        // callback completes), so the service is parked and shutting it down
        // is the only signal we have for "callback exited cleanly".
        await setup.Client.Received(1).ReportDeployedStateAsync(
            Arg.Any<IReadOnlyList<OperatorDeployedDeploymentSiteReportDto>>());

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReportDeployedDeploymentSiteAsync_CloudConnected_SendsSingleEntryReport()
    {
        // The per-deployment-site path called from DeploymentSiteService.RegisterDeploymentSiteAsync after
        // a late-arriving CR is reconciled. Wraps the call in a 1-element
        // OperatorDeployedDeploymentSiteReportDto array so it goes through the same
        // controller-side handler as the bulk reverse-sync.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.IsAlive.Returns(true);

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        setup.Client.ClearReceivedCalls();
        await ((IOperatorHubInvoker)Service).ReportDeployedDeploymentSiteAsync("tenant-a", "65d5c447b420da3fb12381a1");

        await setup.Client.Received(1).ReportDeployedStateAsync(
            Arg.Is<IReadOnlyList<OperatorDeployedDeploymentSiteReportDto>>(reports =>
                reports.Count == 1
                && reports[0].TenantId == "tenant-a"
                && reports[0].DeploymentSiteRtId == "65d5c447b420da3fb12381a1"));

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReportDeployedDeploymentSiteAsync_EdgeMode_IsNoOp()
    {
        // The hub contract rejects edge operators with HubException, so
        // skipping at the source avoids per-CR error audit events on the
        // controller. Same gate as the bulk reverse-sync.
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.IsAlive.Returns(true);

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        setup.Client.ClearReceivedCalls();
        await ((IOperatorHubInvoker)Service).ReportDeployedDeploymentSiteAsync("tenant-a", "65d5c447b420da3fb12381a1");

        await setup.Client.DidNotReceiveWithAnyArgs().ReportDeployedStateAsync(default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReportDeployedDeploymentSiteAsync_HubDisconnected_IsNoOp()
    {
        // Hub connection lost between RegisterDeploymentSiteAsync succeeding (which
        // probably happened against the prior connection) and this call.
        // No-op rather than throw; DeploymentSiteService's RegisterDeploymentSiteAsync only
        // calls this when deployment site.IsRegistered == true anyway, but be
        // defensive in case IsAlive flips mid-sequence.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.IsAlive.Returns(false);

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        setup.Client.ClearReceivedCalls();
        await ((IOperatorHubInvoker)Service).ReportDeployedDeploymentSiteAsync("tenant-a", "65d5c447b420da3fb12381a1");

        await setup.Client.DidNotReceiveWithAnyArgs().ReportDeployedStateAsync(default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    private ClientSetup SetupClient()
    {
        var client = Substitute.For<IOperatorHubClient>();
        ClientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>())
            .Returns(client);

        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));

        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedDeploymentSiteDto>());

        var connectedAndReconnectEnabled = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>()))
            .Do(_ => connectedAndReconnectEnabled.TrySetResult());

        ClientFactory.ClearReceivedCalls();
        client.ClearReceivedCalls();

        return new ClientSetup(client, connectedAndReconnectEnabled);
    }

    private sealed record ClientSetup(IOperatorHubClient Client, TaskCompletionSource ConnectedAndReconnectEnabled);
}
