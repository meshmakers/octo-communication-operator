using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

/// <summary>
///     AB#6418 — an edge operator must not log "Registered with controller, N deployed Cloud
///     deployment sites" as if it owned them.
/// </summary>
public class RegisteredLogTests
{
    [Test]
    [Arguments(false, "as edge operator", "ignored here")]
    [Arguments(true, "as central operator", "to manage")]
    public async Task OnConnect_LogsTheRoleOfTheOperatorForTheListedCloudSites(bool central, string role, string tail)
    {
        var logger = new CapturingLogger();
        var deploymentSiteService = Substitute.For<IDeploymentSiteService>();
        deploymentSiteService.GetDeploymentSites().Returns(Array.Empty<DeploymentSite>());
        var provider = new ServiceCollection().AddSingleton(deploymentSiteService).BuildServiceProvider();
        var clientFactory = Substitute.For<IOperatorHubClientFactory>();
        var client = Substitute.For<IOperatorHubClient>();
        clientFactory.Create(Arg.Any<OperatorHubClientOptions>(), Arg.Any<IOperatorHubCallbacks>()).Returns(client);
        client.StartAsync(Arg.Any<Func<bool, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await ci.Arg<Func<bool, Task>>()(false));
        client.RegisterOperatorAsync(Arg.Any<bool?>()).Returns(new[]
        {
            new DeployedDeploymentSiteDto { TenantId = "tenant-a", DeploymentSiteRtId = "65d5c447b420da3fb12381a1" },
        });
        var connected = new TaskCompletionSource();
        client.When(c => c.EnableReconnect(Arg.Any<Func<bool, Task>>())).Do(_ => connected.TrySetResult());

        var options = new OperatorOptions
        {
            AutoManageDeploymentSites = central,
            CommunicationControllerUri = "https://controller",
        };
        using var service = new OperatorHubService(logger, Microsoft.Extensions.Options.Options.Create(options),
            clientFactory, Substitute.For<IDeploymentSiteManager>(), Substitute.For<IWorkloadReconciler>(), provider);

        var hosted = (IHostedService)service;
        await hosted.StartAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hosted.StopAsync(CancellationToken.None);

        var line = logger.Messages.Single(m => m.StartsWith("Registered with controller"));
        await Assert.That(line).Contains(role);
        await Assert.That(line).Contains(tail);
        await Assert.That(line).Contains("1");
    }

    private sealed class CapturingLogger : ILogger<OperatorHubService>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
