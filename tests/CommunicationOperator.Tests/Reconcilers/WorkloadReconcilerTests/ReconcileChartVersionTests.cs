using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#4955: an empty ChartVersion means "newest in the repository", resolved by helm at
///     upgrade time. On a deploy a human triggered that is the request — but the controller also
///     re-dispatches stranded Pending workloads on every pool re-registration (AB#4894), which
///     happens on operator restarts, blueprint re-applies and CK-model updates. Resolving anew
///     there moved six prod accounting workloads from chart 1.0.71 to 1.0.72 with nobody
///     deploying them. A reconciliation must therefore land on the version already installed.
/// </summary>
internal class ReconcileChartVersionTests : WorkloadReconcilerTestsBase
{
    private const string ChartName = "voest-app";

    private static WorkloadDeployedDto Dto(string chartVersion, bool isReconciliation) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = ChartName,
        ChartVersion = chartVersion,
        IsReconciliation = isReconciliation,
    };

    private Task DeployedWith(string version) => Helm.Received(1).UpgradeInstallAsync(
        Arg.Any<string>(), Arg.Any<string>(), version, Arg.Any<string>(),
        Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
        Arg.Any<CancellationToken>());

    [Test]
    public async Task Reconcile_Unpinned_KeepsTheInstalledChartVersion()
    {
        Helm.GetInstalledChartVersionAsync(Arg.Any<string>(), ChartName, PoolNamespace,
            Arg.Any<CancellationToken>()).Returns("1.0.71");

        await Reconciler.DeployAsync(Dto(string.Empty, isReconciliation: true), CancellationToken.None);

        await DeployedWith("1.0.71");
    }

    [Test]
    public async Task Reconcile_Unpinned_PinsThePreflightToo()
    {
        // The dry-run renders the same chart the real install will apply; letting it resolve a
        // different version would validate something other than what gets deployed.
        Helm.GetInstalledChartVersionAsync(Arg.Any<string>(), ChartName, PoolNamespace,
            Arg.Any<CancellationToken>()).Returns("1.0.71");

        await Reconciler.DeployAsync(Dto(string.Empty, isReconciliation: true), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallDryRunAsync(
            Arg.Any<string>(), Arg.Any<string>(), "1.0.71", Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconcile_Unpinned_NothingInstalled_FallsBackToNewest()
    {
        // A reconcile for a release that was never installed is a first install; there is no
        // previous version to preserve, so "newest" is the only available answer.
        Helm.GetInstalledChartVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns((string?)null);

        await Reconciler.DeployAsync(Dto(string.Empty, isReconciliation: true), CancellationToken.None);

        await DeployedWith(string.Empty);
    }

    [Test]
    public async Task Reconcile_Unpinned_LookupFails_DoesNotFailTheDeploy()
    {
        // Best effort: recovering the stranded workload matters more than pinning it.
        Helm.GetInstalledChartVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("helm unavailable"));

        await Reconciler.DeployAsync(Dto(string.Empty, isReconciliation: true), CancellationToken.None);

        await DeployedWith(string.Empty);
    }

    [Test]
    public async Task Reconcile_Pinned_UsesThePinAndNeverAsksHelm()
    {
        await Reconciler.DeployAsync(Dto("1.2.3", isReconciliation: true), CancellationToken.None);

        await DeployedWith("1.2.3");
        await Helm.DidNotReceiveWithAnyArgs().GetInstalledChartVersionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UserTriggeredDeploy_Unpinned_StillResolvesTheNewestChart()
    {
        // The contract that System.Communication.MainLatest depends on: an explicit deploy with an
        // empty ChartVersion tracks the channel. Pinning it here would break dev/test rollouts.
        Helm.GetInstalledChartVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns("1.0.71");

        await Reconciler.DeployAsync(Dto(string.Empty, isReconciliation: false), CancellationToken.None);

        await DeployedWith(string.Empty);
        await Helm.DidNotReceiveWithAnyArgs().GetInstalledChartVersionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
