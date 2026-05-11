using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal abstract class WorkloadReconcilerTestsBase
{
    protected const string TenantId = "acme";
    protected const string PoolName = "default";
    protected const string WorkloadName = "voest-app";
    protected const string PoolNamespace = "octo";

    protected readonly IHelmRunner Helm;
    protected readonly ICommunicationPoolKubernetesGateway Gateway;
    protected readonly OperatorOptions Options;
    protected readonly WorkloadReconciler Reconciler;

    protected WorkloadReconcilerTestsBase()
    {
        Helm = Substitute.For<IHelmRunner>();
        Gateway = Substitute.For<ICommunicationPoolKubernetesGateway>();
        Options = new OperatorOptions { PoolNamespace = PoolNamespace };
        Reconciler = new WorkloadReconciler(
            Helm,
            Gateway,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<WorkloadReconciler>.Instance);
    }
}
