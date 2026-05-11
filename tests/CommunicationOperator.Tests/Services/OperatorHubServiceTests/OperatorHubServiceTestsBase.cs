using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public abstract class OperatorHubServiceTestsBase : IDisposable
{
    protected const string TenantId = "acme";

    protected readonly ICommunicationPoolManager PoolManager;
    protected readonly IOperatorHubClientFactory ClientFactory;
    protected readonly IWorkloadReconciler WorkloadReconciler;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly OperatorHubService Service;

    protected OperatorHubServiceTestsBase()
    {
        PoolManager = Substitute.For<ICommunicationPoolManager>();
        ClientFactory = Substitute.For<IOperatorHubClientFactory>();
        WorkloadReconciler = Substitute.For<IWorkloadReconciler>();
        OperatorOptions = new OperatorOptions();

        Service = new OperatorHubService(
            NullLogger<OperatorHubService>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions),
            ClientFactory,
            PoolManager,
            WorkloadReconciler);
    }

    public void Dispose()
    {
        Service.Dispose();
        GC.SuppressFinalize(this);
    }
}
