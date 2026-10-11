using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#6471: a controller restart while deploys are in flight made the same workloads deploy twice
///     (two helm revisions) and report <c>Error</c> although helm had reached <c>deployed</c>. The
///     controller re-dispatches every <c>Pending</c> workload when the operator re-registers (AB#4894), and
///     it cannot know whether the operator is still running the first dispatch, has finished it with the
///     report lost, or never received it. Dispatch must therefore be idempotent on the operator side.
/// </summary>
internal class DuplicateDispatchTests : WorkloadReconcilerTestsBase
{
    private static WorkloadDeployedDto Dto(bool isReconciliation = false, string chartVersion = "1.2.3",
        string workloadRtId = WorkloadRtId) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = workloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "voest-app",
        ChartVersion = chartVersion,
        ValuesYaml = string.Empty,
        Values = Array.Empty<ValueOverrideDto>(),
        IsReconciliation = isReconciliation,
    };

    private string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    private string StampOf(WorkloadDeployedDto dto) =>
        WorkloadReconciler.DeployStampPrefix + WorkloadReconciler.DesiredStateFingerprint(dto, Options);

    private int InstallCalls => Helm.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IHelmRunner.UpgradeInstallAsync));

    /// <summary>Makes the real install block until the returned source is completed.</summary>
    private TaskCompletionSource HoldInstall(out TaskCompletionSource installStarted)
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        installStarted = started;
        Helm.UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
            });
        return release;
    }

    [Test]
    public async Task SameDeployWhileRunning_JoinsIt_OneHelmRun_BothSucceed()
    {
        // The pre-fix behaviour: the second dispatch threw "already in progress" and the operator
        // reported Error for a release that was about to be deployed.
        var release = HoldInstall(out var started);

        var first = Reconciler.DeployAsync(Dto(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = Reconciler.DeployAsync(Dto(isReconciliation: true), CancellationToken.None);
        await Task.Delay(100);
        await Assert.That(second.IsCompleted).IsFalse();

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task JoinedDeploy_RunningOneFails_JoinerSeesTheFailure()
    {
        // The joiner reports the real outcome, not a made-up one.
        var started = new TaskCompletionSource();
        var fail = new TaskCompletionSource();
        Helm.UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await fail.Task;
                throw new HelmException("upgrade --install rel", 1, "", "boom");
            });

        var first = Reconciler.DeployAsync(Dto(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Reconciler.DeployAsync(Dto(isReconciliation: true), CancellationToken.None);

        fail.SetResult();

        await Assert.That(async () => await first).Throws<HelmException>();
        await Assert.That(async () => await second).Throws<HelmException>();
        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DifferentDesiredStateWhileRunning_WaitsThenDeploysItself()
    {
        var release = HoldInstall(out var started);

        var first = Reconciler.DeployAsync(Dto(chartVersion: "1.2.3"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = Reconciler.DeployAsync(Dto(chartVersion: "1.2.4"), CancellationToken.None);
        await Task.Delay(100);
        await Assert.That(InstallCalls).IsEqualTo(1);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        // Two different requests: two helm runs, strictly one after the other.
        await Assert.That(InstallCalls).IsEqualTo(2);
        await Helm.Received(1).UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), "1.2.4", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OtherWorkloadsAreNotSerialised()
    {
        // Single-flight is per release: a controller re-dispatching N workloads must not queue them up.
        var release = HoldInstall(out var started);

        var first = Reconciler.DeployAsync(Dto(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var other = Reconciler.DeployAsync(Dto(workloadRtId: "65d5c447b420da3fb12381b2"), CancellationToken.None);
        await Task.Delay(100);
        await Assert.That(InstallCalls).IsEqualTo(2);

        release.SetResult();
        await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Deploy_StampsTheDesiredStateOnTheHelmRevision()
    {
        var dto = Dto();

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            StampOf(dto), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconciliation_AlreadyDeployedWithSameStamp_NoHelmRun()
    {
        // Controller restarted after helm finished but before the report landed: the workload is still
        // Pending, the re-dispatch arrives, helm already runs exactly this state.
        var dto = Dto(isReconciliation: true);
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(40, "deployed", StampOf(dto)));

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(0);
        await Helm.DidNotReceiveWithAnyArgs().UpgradeInstallDryRunAsync(default!, default!, default!, default!,
            default!, default!, default);
    }

    [Test]
    public async Task Reconciliation_DeployedWithOtherStamp_Deploys()
    {
        // The lost dispatch carried a different state than what helm runs: the reconcile must apply it.
        var dto = Dto(isReconciliation: true);
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(40, "deployed", WorkloadReconciler.DeployStampPrefix + "0123"));

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Reconciliation_DeployedWithoutStamp_Deploys()
    {
        // A release deployed by an operator that pre-dates the stamp: unknown state, so deploy.
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(40, "deployed", "Upgrade complete"));

        await Reconciler.DeployAsync(Dto(isReconciliation: true), CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Reconciliation_SameStampButNotDeployed_Deploys()
    {
        // A failed revision stamped with the state is not "already deployed".
        var dto = Dto(isReconciliation: true);
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(40, "failed", StampOf(dto)));

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task UserTriggeredDeploy_NeverSkipsOnTheStamp()
    {
        // A deploy a human asked for always runs: an empty ChartVersion means "newest" (AB#4955).
        var dto = Dto(isReconciliation: false);
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(40, "deployed", StampOf(dto)));

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Reconciliation_HistoryUnreadable_DeploysAsBefore()
    {
        Helm.GetLatestReleaseRevisionAsync(Release, DeploymentSiteNamespace, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));

        await Reconciler.DeployAsync(Dto(isReconciliation: true), CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Fingerprint_IgnoresTheReconciliationFlag_ButNotTheState()
    {
        var plain = WorkloadReconciler.DesiredStateFingerprint(Dto(isReconciliation: false), Options);
        var reconcile = WorkloadReconciler.DesiredStateFingerprint(Dto(isReconciliation: true), Options);
        var other = WorkloadReconciler.DesiredStateFingerprint(Dto(chartVersion: "9.9.9"), Options);

        await Assert.That(reconcile).IsEqualTo(plain);
        await Assert.That(other).IsNotEqualTo(plain);
    }

    [Test]
    public async Task AfterAFailedDeploy_TheNextDispatchStartsFresh()
    {
        // The flight must be released on failure, or the workload could never be deployed again.
        Helm.UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new HelmException("upgrade --install rel", 1, "", "boom")),
                _ => Task.CompletedTask);

        await Assert.That(async () => await Reconciler.DeployAsync(Dto(), CancellationToken.None))
            .Throws<HelmException>();
        await Reconciler.DeployAsync(Dto(), CancellationToken.None);

        await Assert.That(InstallCalls).IsEqualTo(2);
    }
}
