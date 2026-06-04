using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal abstract class WorkloadReconcilerTestsBase
{
    protected const string TenantId = "acme";
    protected const string PoolName = "default";
    protected const string PoolRtId = "65d5c447b420da3fb12381a1";
    protected const string WorkloadName = "voest-app";
    protected const string WorkloadRtId = "65d5c447b420da3fb12381b1";
    protected const string PoolNamespace = "octo";

    protected readonly IHelmRunner Helm;
    protected readonly ICommunicationPoolKubernetesGateway Gateway;
    protected readonly IWorkloadDiagnosticsCollector Diagnostics;
    protected readonly IOperatorHubInvoker Hub;
    protected readonly IServiceProvider ServiceProvider;
    protected readonly OperatorOptions Options;
    protected readonly WorkloadReconciler Reconciler;

    protected WorkloadReconcilerTestsBase()
    {
        Helm = Substitute.For<IHelmRunner>();
        Gateway = Substitute.For<ICommunicationPoolKubernetesGateway>();
        Diagnostics = Substitute.For<IWorkloadDiagnosticsCollector>();
        // Default: collector returns nothing, so a HelmException from the
        // real install propagates verbatim without enrichment. Individual
        // tests override this to assert the enrichment path.
        Diagnostics.CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        Hub = Substitute.For<IOperatorHubInvoker>();
        // Lazy hub resolution mirrors the production wiring (Program.cs uses
        // IServiceProvider to break the OperatorHubService<->WorkloadReconciler
        // singleton cycle). Tests don't exercise the cycle, so a thin
        // ServiceCollection is enough.
        ServiceProvider = new ServiceCollection()
            .AddSingleton(Hub)
            .BuildServiceProvider();
        Options = new OperatorOptions { PoolNamespace = PoolNamespace };
        Reconciler = new WorkloadReconciler(
            Helm,
            Gateway,
            Diagnostics,
            ServiceProvider,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<WorkloadReconciler>.Instance);
    }
}
