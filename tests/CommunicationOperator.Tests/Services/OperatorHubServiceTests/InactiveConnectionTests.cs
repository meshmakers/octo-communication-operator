using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

/// <summary>
///     AB#6418 — while the hub connection is Connecting/Reconnecting <c>IsAlive</c> is true but a hub
///     invocation throws "The 'InvokeCoreAsync' method cannot be called if the connection is not
///     active". That is "hub down", not a failed reconcile: the connect callback registers the site.
/// </summary>
public class InactiveConnectionTests : OperatorHubServiceTestsBase
{
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381a1";
    private const string NotActive = "The 'InvokeCoreAsync' method cannot be called if the connection is not active";

    private async Task<IOperatorHubClient> StartedClientAsync()
    {
        OperatorOptions.AutoManageDeploymentSites = false;
        OperatorOptions.CommunicationControllerUri = "https://controller";
        var client = Substitute.For<IOperatorHubClient>();
        ClientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>()).Returns(client);
        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));
        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(Array.Empty<DeployedDeploymentSiteDto>());
        client.IsAlive.Returns(true);
        var connected = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>())).Do(_ => connected.TrySetResult());

        await ((IHostedService)Service).StartAsync(CancellationToken.None);
        await connected.Task;
        return client;
    }

    [Test]
    public async Task RegisterDeploymentSite_ConnectionNotActive_IsDeferredNotFailed()
    {
        var client = await StartedClientAsync();
        client.RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId).Returns(Task.FromException(new InvalidOperationException(NotActive)));

        var registered = await ((IOperatorHubInvoker)Service).RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Assert.That(registered).IsFalse();
        await client.Received(1).RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task UnregisterDeploymentSite_ConnectionNotActive_IsSkippedNotFailed()
    {
        var client = await StartedClientAsync();
        client.UnregisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId).Returns(Task.FromException(new InvalidOperationException(NotActive)));

        await ((IOperatorHubInvoker)Service).UnregisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RegisterDeploymentSite_OtherInvalidOperation_StillFails()
    {
        var client = await StartedClientAsync();
        client.RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId).Returns(Task.FromException(new InvalidOperationException("something else")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IOperatorHubInvoker)Service).RegisterDeploymentSiteAsync(TenantId, DeploymentSiteRtId));

        await ((IHostedService)Service).StopAsync(CancellationToken.None);
    }
}
