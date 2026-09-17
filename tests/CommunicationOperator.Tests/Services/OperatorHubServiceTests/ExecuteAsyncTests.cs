using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class ExecuteAsyncTests : OperatorHubServiceTestsBase
{
    [Test]
    public async Task ExecuteAsync_AutoManageDeploymentSitesDisabledButControllerUriSet_StillCreatesClient()
    {
        // Regression: previously the service short-circuited when AutoManageDeploymentSites=false,
        // which meant the edge operator never opened a SignalR connection and deployment sites
        // claimed by edge-cluster CRs stayed Unregistered forever. AutoManageDeploymentSites only
        // gates auto-CR-creation; the hub connection itself is required in both modes.
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        ClientFactory.Received(1).Create(
            Arg.Is<OperatorHubClientOptions>(o => o.EndpointUri == "https://controller"),
            Service);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_ControllerUriMissing_DoesNotCreateClient()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "";

        await StartAndStopAsync();

        ClientFactory.DidNotReceive().Create(
            Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>());
    }

    [Test]
    public async Task ExecuteAsync_AutoManaged_CreatesClientWithControllerUriAndService()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        ClientFactory.Received(1).Create(
            Arg.Is<OperatorHubClientOptions>(o => o.EndpointUri == "https://controller"),
            Service);
        setup.Client.Received(1).EnableReconnect(Arg.Any<Func<bool, Task>>());
        await setup.Client.Received(1).StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>());

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_RegistersOperatorAndCreatesEachDeployedDeploymentSite()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(new[]
        {
            new DeployedDeploymentSiteDto { TenantId = "tenant-a", DeploymentSiteRtId = "65d5c447b420da3fb12381a1" },
            new DeployedDeploymentSiteDto { TenantId = "tenant-b", DeploymentSiteRtId = "65d5c447b420da3fb12381a2" }
        });

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(Arg.Any<bool?>());
        await DeploymentSiteManager.Received(1).CreateDeploymentSiteAsync("tenant-a", "65d5c447b420da3fb12381a1");
        await DeploymentSiteManager.Received(1).CreateDeploymentSiteAsync("tenant-b", "65d5c447b420da3fb12381a2");

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_DeclaresAutoManageDeploymentSitesToController()
    {
        // The controller now uses this declaration to validate that the operator
        // does not claim deployment sites whose Environment doesn't match its mode. We must
        // forward _options.AutoManageDeploymentSites verbatim on every (re)connect.
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>())
            .Returns(Array.Empty<DeployedDeploymentSiteDto>());

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(false);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_CentralMode_DeclaresTrueToController()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>())
            .Returns(Array.Empty<DeployedDeploymentSiteDto>());

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(true);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_EdgeMode_DoesNotCreateCrsForDeployedCloudDeploymentSites()
    {
        // Regression: a reboot of an edge device used to materialize a
        // DeploymentSite CR (and broker secret) for every Cloud deployment site the
        // controller's RegisterOperatorAsync returned, even though
        // AutoManageDeploymentSites=false. Once the KubeOps reconciler picked up that
        // CR the edge operator also registered itself as the deployment site owner,
        // and workload-deploy events started routing to the edge cluster
        // alongside the central one. The reconnect path must apply the same
        // gate that DeploymentSiteDeployedAsync already does.
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(new[]
        {
            new DeployedDeploymentSiteDto { TenantId = "tenant-a", DeploymentSiteRtId = "65d5c447b420da3fb12381a1" },
            new DeployedDeploymentSiteDto { TenantId = "tenant-b", DeploymentSiteRtId = "65d5c447b420da3fb12381a2" }
        });

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(Arg.Any<bool?>());
        await DeploymentSiteManager.DidNotReceiveWithAnyArgs().CreateDeploymentSiteAsync(default!, default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_StopsClientOnShutdown()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;
        await hosted.StopAsync(CancellationToken.None);

        await setup.Client.Received(1).StopAsync();
    }

    private ClientSetup SetupClient()
    {
        var client = Substitute.For<IOperatorHubClient>();
        ClientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>())
            .Returns(client);

        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));

        // RegisterOperatorAsync default is empty so the foreach doesn't NRE.
        // Arg.Any<bool?>() matches both the explicit-mode (true/false) and the
        // legacy (null) overload — production code passes _options.AutoManageDeploymentSites.
        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedDeploymentSiteDto>());

        var connectedAndReconnectEnabled = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>()))
            .Do(_ => connectedAndReconnectEnabled.TrySetResult());

        ClientFactory.ClearReceivedCalls();
        client.ClearReceivedCalls();

        return new ClientSetup(client, connectedAndReconnectEnabled);
    }

    private async Task StartAndStopAsync()
    {
        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed record ClientSetup(IOperatorHubClient Client, TaskCompletionSource ConnectedAndReconnectEnabled);
}
