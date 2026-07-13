using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

/// <summary>
/// Regression tests for AB#4371: a pool registration the controller rejects
/// while the SignalR connection stays alive used to be logged and forgotten.
/// The pool then stayed orphaned — no reconnect ever fired, and the
/// controller dropped every workload deploy/undeploy for it. The periodic
/// registration retry loop closes that gap.
/// </summary>
public class RegistrationRetryTests : OperatorHubServiceTestsBase
{
    private const string PoolRtId = "65d5c447b420da3fb12381a1";

    private static Pool MakePool(string tenantId, string poolRtId)
    {
        var entity = new V1CommunicationPoolEntity
        {
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                TenantId = tenantId,
                PoolRtId = poolRtId,
            },
        };
        return new Pool(new K8Pool { TenantId = tenantId, PoolRtId = poolRtId, Namespace = "octo" }, entity);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // Bounded poll; the retry loop runs at PoolRegistrationRetrySeconds
        // (milliseconds in these tests), so a couple of seconds is plenty.
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        await Assert.That(condition()).IsTrue();
    }

    [Test]
    public async Task RegistrationRejectedOnConnect_IsRetriedUntilTheControllerAccepts()
    {
        // The prod-1 incident shape: all pods restart together, the operator
        // reconnects while the controller's CkCache is still importing tenant
        // models, RegisterPoolAsync throws once — the retry loop must recover
        // the pool once the controller accepts the call.
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.PoolRegistrationRetrySeconds = 0.02;

        var pool = MakePool(TenantId, PoolRtId);
        PoolService.GetPools().Returns(new[] { pool });

        var setup = SetupClient();
        var registerCalls = 0;
        setup.Client.RegisterPoolAsync(TenantId, PoolRtId).Returns(_ =>
            Interlocked.Increment(ref registerCalls) == 1
                ? Task.FromException(new HubException("Failed to get pools"))
                : Task.CompletedTask);

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await WaitUntilAsync(() => pool.IsRegistered);
        await Assert.That(registerCalls).IsGreaterThanOrEqualTo(2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RecoveredPool_GetsPerPoolReverseSync()
    {
        // A pool that was orphaned may also carry a drifted DeploymentState;
        // recovery must trigger the same per-pool reverse-sync that a normal
        // CR reconcile fires.
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.PoolRegistrationRetrySeconds = 0.02;

        var pool = MakePool(TenantId, PoolRtId);
        PoolService.GetPools().Returns(new[] { pool });

        var setup = SetupClient();
        var registerCalls = 0;
        setup.Client.RegisterPoolAsync(TenantId, PoolRtId).Returns(_ =>
            Interlocked.Increment(ref registerCalls) == 1
                ? Task.FromException(new HubException("Failed to get pools"))
                : Task.CompletedTask);

        var reportCalls = 0;
        setup.Client.When(c => c.ReportDeployedStateAsync(
                Arg.Is<IReadOnlyList<OperatorDeployedPoolReportDto>>(reports =>
                    reports.Count == 1
                    && reports[0].TenantId == TenantId
                    && reports[0].PoolRtId == PoolRtId)))
            .Do(_ => Interlocked.Increment(ref reportCalls));

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        // The bulk reverse-sync on connect always reports the owned pool once;
        // the recovery must add a second, per-pool report.
        await WaitUntilAsync(() => pool.IsRegistered && reportCalls >= 2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ConnectCallback_ResetsRegistrationStateBeforeReplaying()
    {
        // A pool registered on a previous connection must not keep a stale
        // IsRegistered=true when its re-registration on the new connection
        // fails — otherwise the retry loop cannot see it.
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        PoolService.Received(1).ResetRegistrationState();

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RetryDisabled_DoesNotRetry()
    {
        OperatorOptions.AutoManagePools = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.PoolRegistrationRetrySeconds = 0;

        var pool = MakePool(TenantId, PoolRtId);
        PoolService.GetPools().Returns(new[] { pool });

        var setup = SetupClient();
        setup.Client.RegisterPoolAsync(TenantId, PoolRtId)
            .Returns(Task.FromException(new HubException("Failed to get pools")));

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        // Give a would-be retry loop ample time to fire, then verify only the
        // connect-callback registration attempt happened.
        await Task.Delay(200);
        await setup.Client.Received(1).RegisterPoolAsync(TenantId, PoolRtId);
        await Assert.That(pool.IsRegistered).IsFalse();

        await hosted.StopAsync(CancellationToken.None);
    }

    private ClientSetup SetupClient()
    {
        var client = Substitute.For<IOperatorHubClient>();
        ClientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>())
            .Returns(client);

        client.IsAlive.Returns(true);

        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));

        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedPoolDto>());

        var connectedAndReconnectEnabled = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>()))
            .Do(_ => connectedAndReconnectEnabled.TrySetResult());

        ClientFactory.ClearReceivedCalls();
        client.ClearReceivedCalls();

        return new ClientSetup(client, connectedAndReconnectEnabled);
    }

    private sealed record ClientSetup(IOperatorHubClient Client, TaskCompletionSource ConnectedAndReconnectEnabled);
}
