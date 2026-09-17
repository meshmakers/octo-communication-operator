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
/// Regression tests for AB#4371: a deployment site registration the controller rejects
/// while the SignalR connection stays alive used to be logged and forgotten.
/// The deployment site then stayed orphaned — no reconnect ever fired, and the
/// controller dropped every workload deploy/undeploy for it. The periodic
/// registration retry loop closes that gap.
/// </summary>
public class RegistrationRetryTests : OperatorHubServiceTestsBase
{
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381a1";

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // Bounded poll; the retry loop runs at DeploymentSiteRegistrationRetrySeconds
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
        // models, RegisterDeploymentSiteAsync throws once — the retry loop must recover
        // the deployment site once the controller accepts the call.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.DeploymentSiteRegistrationRetrySeconds = 0.02;

        var deploymentSite = MakeDeploymentSite(TenantId, DeploymentSiteRtId);
        DeploymentSiteService.GetDeploymentSites().Returns(new[] { deploymentSite });

        var setup = SetupClient();
        var registerCalls = 0;
        setup.Client.RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId).Returns(_ =>
            Interlocked.Increment(ref registerCalls) == 1
                ? Task.FromException(new HubException("Failed to get deployment sites"))
                : Task.CompletedTask);

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        await WaitUntilAsync(() => deploymentSite.IsRegistered);
        await Assert.That(registerCalls).IsGreaterThanOrEqualTo(2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RecoveredDeploymentSite_GetsPerDeploymentSiteReverseSync()
    {
        // A deployment site that was orphaned may also carry a drifted DeploymentState;
        // recovery must trigger the same per-deployment-site reverse-sync that a normal
        // CR reconcile fires.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.DeploymentSiteRegistrationRetrySeconds = 0.02;

        var deploymentSite = MakeDeploymentSite(TenantId, DeploymentSiteRtId);
        DeploymentSiteService.GetDeploymentSites().Returns(new[] { deploymentSite });

        var setup = SetupClient();
        var registerCalls = 0;
        setup.Client.RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId).Returns(_ =>
            Interlocked.Increment(ref registerCalls) == 1
                ? Task.FromException(new HubException("Failed to get deployment sites"))
                : Task.CompletedTask);

        var reportCalls = 0;
        setup.Client.When(c => c.ReportDeployedStateAsync(
                Arg.Is<IReadOnlyList<OperatorDeployedDeploymentSiteReportDto>>(reports =>
                    reports.Count == 1
                    && reports[0].TenantId == TenantId
                    && reports[0].DeploymentSiteRtId == DeploymentSiteRtId)))
            .Do(_ => Interlocked.Increment(ref reportCalls));

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        // The bulk reverse-sync on connect always reports the owned deployment site once;
        // the recovery must add a second, per-deployment-site report.
        await WaitUntilAsync(() => deploymentSite.IsRegistered && reportCalls >= 2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ConnectCallback_ResetsRegistrationStateBeforeReplaying()
    {
        // A deployment site registered on a previous connection must not keep a stale
        // IsRegistered=true when its re-registration on the new connection
        // fails — otherwise the retry loop cannot see it.
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";

        var setup = SetupClient();

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        DeploymentSiteService.Received(1).ResetRegistrationState();

        await hosted.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RetryDisabled_DoesNotRetry()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        OperatorOptions.DeploymentSiteRegistrationRetrySeconds = 0;

        var deploymentSite = MakeDeploymentSite(TenantId, DeploymentSiteRtId);
        DeploymentSiteService.GetDeploymentSites().Returns(new[] { deploymentSite });

        var setup = SetupClient();
        setup.Client.RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(Task.FromException(new HubException("Failed to get deployment sites")));

        var hosted = (IHostedService)Service;
        await hosted.StartAsync(CancellationToken.None);
        await setup.ConnectedAndReconnectEnabled.Task;

        // Give a would-be retry loop ample time to fire, then verify only the
        // connect-callback registration attempt happened.
        await Task.Delay(200);
        await setup.Client.Received(1).RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
        await Assert.That(deploymentSite.IsRegistered).IsFalse();

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
