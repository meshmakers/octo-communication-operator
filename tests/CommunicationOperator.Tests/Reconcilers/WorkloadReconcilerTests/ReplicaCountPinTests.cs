using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#5350 — the deploy pins <c>replicaCount</c> to what is already running, so that Helm 4's
///     server-side apply is handed the value it would otherwise fight over.
///
///     <para>
///     The AB#4917 scale verb writes <c>spec.replicas</c> outside helm, and the chart renders that
///     same field from <c>replicaCount</c>. Server-side apply refuses to change a field another
///     manager owns — so every scaled workload failed its next deploy, and helm's rollback failed
///     the same way and left the release <c>failed</c>. Applying the value that is already there is
///     accepted instead.
///     </para>
///
///     <para>
///     🔴 What these tests pin is the <b>decision</b>, not the mechanism: one <c>--set</c> for one
///     rule, the same value on the pre-flight and the real install, and the three cases with no
///     obvious answer (no Deployments, several Deployments disagreeing, a live 0 nobody hibernated)
///     each answered one way and only one way. That server-side apply really does accept an
///     unchanged value — the premise all of this rests on — is measured against a real apiserver and
///     a real helm binary in <see cref="E2E.ScaledWorkloadRedeployKindE2ETests" />; it cannot be
///     proved here, where there is neither.
///     </para>
/// </summary>
internal class ReplicaCountPinTests : WorkloadReconcilerTestsBase
{
    private const string PlatformNamespace = "octo-platform";

    private static WorkloadDeployedDto Dto(bool hibernated = false,
        WorkloadTypeDto workloadType = WorkloadTypeDto.Adapter) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = workloadType,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "octo-mesh-adapter",
        ChartVersion = "1.2.3",
        Hibernated = hibernated,
    };

    private void LiveReplicas(params int[] replicas) =>
        Gateway.GetDeploymentReplicasByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(replicas);

    /// <summary>
    ///     Both helm calls, not just the real install: the pre-flight exists to answer "would this
    ///     apply", and a pre-flight that renders a different replica count answers it about
    ///     something else.
    /// </summary>
    private async Task AssertPinnedTo(string? expected)
    {
        await Helm.Received(1).UpgradeInstallDryRunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(set => Matches(set, expected)),
            Arg.Any<CancellationToken>());
        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<IReadOnlyDictionary<string, string>>(set => Matches(set, expected)),
            Arg.Any<CancellationToken>());
    }

    private static bool Matches(IReadOnlyDictionary<string, string> setValues, string? expected) =>
        expected == null
            ? !setValues.ContainsKey("replicaCount")
            : setValues.TryGetValue("replicaCount", out var value) && value == expected;

    [Test]
    public async Task Deploy_ReleaseIsScaled_PinsTheLiveCount()
    {
        // The scale verb left three replicas behind and owns spec.replicas; the chart would render
        // whatever replicaCount says. Pinning the live value is what makes the apply a no-op on
        // that field instead of a conflict.
        LiveReplicas(3);

        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await AssertPinnedTo("3");
    }

    [Test]
    public async Task Deploy_ReadsTheLiveCountForTheResolvedNamespaceAndRelease()
    {
        // 🔴 Same namespace and same release name the scale verb patches through — a read that
        // asked a different namespace (a pool lives in the platform namespace) would find no
        // Deployments and quietly fall back to "first install", which is the pre-fix behaviour
        // wearing the fix's clothes.
        Options.PlatformNamespace = PlatformNamespace;
        LiveReplicas(2);

        await Reconciler.DeployAsync(Dto(workloadType: WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Gateway.Received(1).GetDeploymentReplicasByInstanceAsync(
            PlatformNamespace, WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId),
            Arg.Any<CancellationToken>());
        await AssertPinnedTo("2");
    }

    [Test]
    public async Task Deploy_Hibernated_PinsZeroEvenWhenSomethingIsStillRunning()
    {
        // Hibernation is the controller's lifecycle decision and outranks the live value: a
        // replica still running during Draining must not make the deploy resurrect the workload.
        LiveReplicas(3);

        await Reconciler.DeployAsync(Dto(hibernated: true), CancellationToken.None);

        await AssertPinnedTo("0");
    }

    [Test]
    public async Task Deploy_Hibernated_DoesNotEvenAskWhatIsRunning()
    {
        // 🔴 The two rules are one branch, not two competing --set values: hibernation answers the
        // question, so the live count is never read and cannot contribute a second answer whose
        // precedence would then rest on argument order. It also keeps a hibernated redeploy off the
        // apiserver for a value it would ignore.
        LiveReplicas(3);

        await Reconciler.DeployAsync(Dto(hibernated: true), CancellationToken.None);

        await Gateway.DidNotReceiveWithAnyArgs().GetDeploymentReplicasByInstanceAsync(
            default!, default!, default);
    }

    [Test]
    public async Task Deploy_NoDeploymentsYet_DoesNotPin()
    {
        // First install: nothing is running, nothing owns spec.replicas, so the chart and the
        // values layers are the only opinion that exists — and the only one worth honouring.
        LiveReplicas();

        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await AssertPinnedTo(null);
    }

    [Test]
    public async Task Deploy_LiveZeroButNotHibernated_DoesNotPin()
    {
        // 🔴 The one case where the live value is deliberately not honoured. Pinning 0 here would
        // make every future deploy the thing that keeps the workload down, with no deploy able to
        // undo it; the controller does not think it is hibernated, so nothing would wake it either.
        LiveReplicas(0);

        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await AssertPinnedTo(null);
    }

    [Test]
    public async Task Deploy_DeploymentsDisagree_PinsTheLargest()
    {
        // replicaCount is one value for the whole release, so one number has to be chosen. The
        // smallest would scale the busiest Deployment down as a side effect of an unrelated
        // deploy, which is the accident this pin exists to prevent.
        LiveReplicas(1, 4, 2);

        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await AssertPinnedTo("4");
    }

    [Test]
    public async Task Deploy_LiveCountUnreadable_DeploysWithoutAPin()
    {
        // Best effort, like every other cluster-state read on this path: a failed read must not
        // fail the deploy. It falls back to the pre-AB#5350 behaviour, which may hit the conflict —
        // and a deploy that hits a conflict is still better than one that never ran.
        Gateway.GetDeploymentReplicasByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("apiserver down"));

        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await AssertPinnedTo(null);
    }

    [Test]
    public async Task Deploy_LiveCountReadCancelled_DoesNotSwallowTheCancellation()
    {
        // The best-effort catch must not turn a shutdown into a deploy that carries on. Ordering
        // of the two catch clauses is the whole of this test.
        Gateway.GetDeploymentReplicasByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.That(async () => await Reconciler.DeployAsync(Dto(), CancellationToken.None))
            .Throws<OperationCanceledException>();

        await Helm.DidNotReceiveWithAnyArgs().UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}
