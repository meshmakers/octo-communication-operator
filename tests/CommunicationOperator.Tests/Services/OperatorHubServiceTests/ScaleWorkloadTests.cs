using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class ScaleWorkloadTests : OperatorHubServiceTestsBase
{
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";

    private static ScaleWorkloadDto Dto(int replicas = 0) => new()
    {
        TenantId = TenantId,
        PoolRtId = "65d5c447b420da3fb12381a1",
        WorkloadRtId = WorkloadRtId,
        WorkloadName = "meshtest-adapter",
        WorkloadType = WorkloadTypeDto.Adapter,
        Replicas = replicas,
    };

    [Test]
    public async Task NoActiveClient_DoesNotThrow()
    {
        // Service was never started → _client is null. The callback must
        // still invoke the reconciler and tolerate having nobody to ack to.
        WorkloadReconciler.ScaleAsync(Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await Service.ScaleWorkloadAsync(Dto());

        await WorkloadReconciler.Received(1).ScaleAsync(
            Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcilerPatchesDeployments_ReportsSuccessBack()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        WorkloadReconciler.ScaleAsync(Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await Service.ScaleWorkloadAsync(Dto(replicas: 0));

        await setup.Client.Received(1).ReportWorkloadScaleStatusAsync(
            Arg.Is<WorkloadScaleStatusDto>(s =>
                s.TenantId == TenantId
                && s.WorkloadRtId == WorkloadRtId
                && s.Replicas == 0
                && s.Success));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReconcilerPatchesNothing_ReportsFailureBack()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        // Zero patched deployments means the release had no Deployments —
        // the controller's lifecycle state machine must see that as a
        // failed scale, not a silent success.
        WorkloadReconciler.ScaleAsync(Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>())
            .Returns(0);

        await Service.ScaleWorkloadAsync(Dto(replicas: 1));

        await setup.Client.Received(1).ReportWorkloadScaleStatusAsync(
            Arg.Is<WorkloadScaleStatusDto>(s =>
                s.WorkloadRtId == WorkloadRtId
                && s.Replicas == 1
                && !s.Success
                && s.StatusMessage != null));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ReconcilerThrows_ReportsFailureWithMessage()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        WorkloadReconciler.ScaleAsync(Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("apiserver: deployments.apps is forbidden"));

        // Same rule as deploy/undeploy: one bad workload must not crash the
        // hub connection.
        await Service.ScaleWorkloadAsync(Dto());

        await setup.Client.Received(1).ReportWorkloadScaleStatusAsync(
            Arg.Is<WorkloadScaleStatusDto>(s =>
                !s.Success
                && s.StatusMessage != null
                && s.StatusMessage.Contains("deployments.apps is forbidden")));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ScaleStatusReportRejectedWithHubException_IsSwallowed()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var setup = await StartConnectedAsync();

        WorkloadReconciler.ScaleAsync(Arg.Any<ScaleWorkloadDto>(), Arg.Any<CancellationToken>())
            .Returns(1);
        // Older controller builds reject the new hub method. The callback
        // must degrade silently (logged once) instead of crashing.
        setup.Client.ReportWorkloadScaleStatusAsync(Arg.Any<WorkloadScaleStatusDto>())
            .ThrowsAsync(new HubException("Method does not exist on the server"));

        await Service.ScaleWorkloadAsync(Dto());

        // A second scale must still attempt the report (and swallow again).
        await Service.ScaleWorkloadAsync(Dto());

        await setup.Client.Received(2).ReportWorkloadScaleStatusAsync(
            Arg.Any<WorkloadScaleStatusDto>());

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
        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedPoolDto>());

        var connectedAndReconnectEnabled = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>()))
            .Do(_ => connectedAndReconnectEnabled.TrySetResult());

        return new ClientSetup(client, connectedAndReconnectEnabled);
    }

    private sealed record ClientSetup(IOperatorHubClient Client, TaskCompletionSource ConnectedAndReconnectEnabled);
}
