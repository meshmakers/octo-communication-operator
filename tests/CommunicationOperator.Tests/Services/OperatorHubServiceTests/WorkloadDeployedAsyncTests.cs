using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class WorkloadDeployedAsyncTests : OperatorHubServiceTestsBase
{
    private static WorkloadDeployedDto Dto() => new()
    {
        TenantId = TenantId,
        PoolName = "cloud",
        WorkloadName = "meshtest-adapter",
        WorkloadRtId = "66004fda527ac79a03ecedd7",
        WorkloadType = WorkloadTypeDto.Adapter,
        RepositoryUrl = "https://example/charts",
        ChartName = "octo-mesh-adapter",
        ChartVersion = "0.1.31660",
    };

    [Test]
    public async Task NoActiveClient_DoesNotThrow()
    {
        // Service was never started → _client is null. The callback must
        // still tolerate this and not crash the test process.
        await Service.WorkloadDeployedAsync(Dto());

        await WorkloadReconciler.Received(1).DeployAsync(Arg.Any<WorkloadDeployedDto>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcilerSucceeds_ReportsSuccessBack()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        await Service.WorkloadDeployedAsync(Dto());

        await setup.Client.Received(1).ReportWorkloadDeploymentStatusAsync(
            Arg.Is<WorkloadDeploymentStatusDto>(s =>
                s.TenantId == TenantId
                && s.WorkloadRtId == "66004fda527ac79a03ecedd7"
                && s.Success
                && s.StatusMessage == null));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReconcilerThrows_ReportsFailureWithMessage()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        WorkloadReconciler.DeployAsync(Arg.Any<WorkloadDeployedDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("helm: secrets.databaseUser does not exist"));

        await Service.WorkloadDeployedAsync(Dto());

        await setup.Client.Received(1).ReportWorkloadDeploymentStatusAsync(
            Arg.Is<WorkloadDeploymentStatusDto>(s =>
                !s.Success
                && s.StatusMessage != null
                && s.StatusMessage.Contains("secrets.databaseUser")));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StatusReportThrows_DoesNotPropagate()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        setup.Client.ReportWorkloadDeploymentStatusAsync(Arg.Any<WorkloadDeploymentStatusDto>())
            .ThrowsAsync(new InvalidOperationException("hub gone"));

        // Both the reconciler and the status report could throw. The
        // callback must swallow both so the SignalR hub stays alive.
        await Service.WorkloadDeployedAsync(Dto());

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    private async Task<ClientSetup> StartConnectedAsync()
    {
        var setup = SetupClient();
        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;
        return setup;
    }

    private ClientSetup SetupClient()
    {
        var client = Substitute.For<IOperatorHubClient>();
        ClientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>())
            .Returns(client);

        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));
        client.RegisterOperatorAsync().Returns(Array.Empty<DeployedPoolDto>());

        var connectedAndReconnectEnabled = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>()))
            .Do(_ => connectedAndReconnectEnabled.TrySetResult());

        return new ClientSetup(client, connectedAndReconnectEnabled);
    }

    private sealed record ClientSetup(IOperatorHubClient Client, TaskCompletionSource ConnectedAndReconnectEnabled);
}
