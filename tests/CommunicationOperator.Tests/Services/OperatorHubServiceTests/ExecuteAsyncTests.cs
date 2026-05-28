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
    public async Task ExecuteAsync_AutoManagePoolsDisabledButControllerUriSet_StillCreatesClient()
    {
        // Regression: previously the service short-circuited when AutoManagePools=false,
        // which meant the edge operator never opened a SignalR connection and pools
        // claimed by edge-cluster CRs stayed Unregistered forever. AutoManagePools only
        // gates auto-CR-creation; the hub connection itself is required in both modes.
        OperatorOptions.AutoManagePools = false;
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
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "";

        await StartAndStopAsync();

        ClientFactory.DidNotReceive().Create(
            Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>());
    }

    [Test]
    public async Task ExecuteAsync_AutoManaged_CreatesClientWithControllerUriAndService()
    {
        OperatorOptions.AutoManagePools = true;
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
    public async Task ExecuteAsync_OnConnect_RegistersOperatorAndCreatesEachDeployedPool()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(new[]
        {
            new DeployedPoolDto { TenantId = "tenant-a", PoolRtId = "65d5c447b420da3fb12381a1" },
            new DeployedPoolDto { TenantId = "tenant-b", PoolRtId = "65d5c447b420da3fb12381a2" }
        });

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(Arg.Any<bool?>());
        await PoolManager.Received(1).CreatePoolAsync("tenant-a", "65d5c447b420da3fb12381a1");
        await PoolManager.Received(1).CreatePoolAsync("tenant-b", "65d5c447b420da3fb12381a2");

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_DeclaresAutoManagePoolsToController()
    {
        // The controller now uses this declaration to validate that the operator
        // does not claim pools whose Environment doesn't match its mode. We must
        // forward _options.AutoManagePools verbatim on every (re)connect.
        OperatorOptions.AutoManagePools = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>())
            .Returns(Array.Empty<DeployedPoolDto>());

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(false);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_CentralMode_DeclaresTrueToController()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>())
            .Returns(Array.Empty<DeployedPoolDto>());

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(true);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_OnConnect_EdgeMode_DoesNotCreateCrsForDeployedCloudPools()
    {
        // Regression: a reboot of an edge device used to materialize a
        // CommunicationPool CR (and broker secret) for every Cloud pool the
        // controller's RegisterOperatorAsync returned, even though
        // AutoManagePools=false. Once the KubeOps reconciler picked up that
        // CR the edge operator also registered itself as the pool owner,
        // and workload-deploy events started routing to the edge cluster
        // alongside the central one. The reconnect path must apply the same
        // gate that PoolDeployedAsync already does.
        OperatorOptions.AutoManagePools = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();
        setup.Client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(new[]
        {
            new DeployedPoolDto { TenantId = "tenant-a", PoolRtId = "65d5c447b420da3fb12381a1" },
            new DeployedPoolDto { TenantId = "tenant-b", PoolRtId = "65d5c447b420da3fb12381a2" }
        });

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await setup.Client.Received(1).RegisterOperatorAsync(Arg.Any<bool?>());
        await PoolManager.DidNotReceiveWithAnyArgs().CreatePoolAsync(default!, default!);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ExecuteAsync_StopsClientOnShutdown()
    {
        OperatorOptions.AutoManagePools = true;
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
        // legacy (null) overload — production code passes _options.AutoManagePools.
        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedPoolDto>());

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
