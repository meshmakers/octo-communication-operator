using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public abstract class OperatorHubServiceTestsBase : IDisposable
{
    protected const string TenantId = "acme";

    protected readonly IDeploymentSiteManager DeploymentSiteManager;
    protected readonly IOperatorHubClientFactory ClientFactory;
    protected readonly IWorkloadReconciler WorkloadReconciler;
    protected readonly IDeploymentSiteService DeploymentSiteService;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly OperatorHubService Service;

    protected OperatorHubServiceTestsBase()
    {
        DeploymentSiteManager = Substitute.For<IDeploymentSiteManager>();
        ClientFactory = Substitute.For<IOperatorHubClientFactory>();
        WorkloadReconciler = Substitute.For<IWorkloadReconciler>();
        DeploymentSiteService = Substitute.For<IDeploymentSiteService>();
        // Default: empty deployment site list so reconnect handler's foreach over
        // GetDeploymentSites() does nothing in the typical unit-test fixture.
        DeploymentSiteService.GetDeploymentSites().Returns(Array.Empty<Meshmakers.Octo.Communication.Operator.Models.DeploymentSite>());
        OperatorOptions = new OperatorOptions();

        var services = new ServiceCollection();
        services.AddSingleton(DeploymentSiteService);
        var serviceProvider = services.BuildServiceProvider();

        Service = new OperatorHubService(
            NullLogger<OperatorHubService>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions),
            ClientFactory,
            DeploymentSiteManager,
            WorkloadReconciler,
            serviceProvider);
    }

    public void Dispose()
    {
        Service.Dispose();
        GC.SuppressFinalize(this);
    }
}
