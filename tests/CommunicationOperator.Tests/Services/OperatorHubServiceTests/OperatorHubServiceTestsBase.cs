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

    protected readonly ICommunicationPoolManager PoolManager;
    protected readonly IOperatorHubClientFactory ClientFactory;
    protected readonly IWorkloadReconciler WorkloadReconciler;
    protected readonly IPoolService PoolService;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly OperatorHubService Service;

    protected OperatorHubServiceTestsBase()
    {
        PoolManager = Substitute.For<ICommunicationPoolManager>();
        ClientFactory = Substitute.For<IOperatorHubClientFactory>();
        WorkloadReconciler = Substitute.For<IWorkloadReconciler>();
        PoolService = Substitute.For<IPoolService>();
        // Default: empty pool list so reconnect handler's foreach over
        // GetPools() does nothing in the typical unit-test fixture.
        PoolService.GetPools().Returns(Array.Empty<Meshmakers.Octo.Communication.Operator.Models.Pool>());
        OperatorOptions = new OperatorOptions();

        var services = new ServiceCollection();
        services.AddSingleton(PoolService);
        var serviceProvider = services.BuildServiceProvider();

        Service = new OperatorHubService(
            NullLogger<OperatorHubService>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions),
            ClientFactory,
            PoolManager,
            WorkloadReconciler,
            serviceProvider);
    }

    public void Dispose()
    {
        Service.Dispose();
        GC.SuppressFinalize(this);
    }
}
