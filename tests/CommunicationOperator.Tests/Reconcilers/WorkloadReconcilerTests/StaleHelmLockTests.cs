using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#4894: a helm process killed mid-upgrade (operator pod replaced by a rollout while a
///     deploy was in flight) leaves the newest release revision in a <c>pending-*</c> status that
///     blocks every later install/upgrade/rollback. Before each deploy the reconciler clears a
///     provably stale lock — and only a provably stale one.
/// </summary>
internal class StaleHelmLockTests : WorkloadReconcilerTestsBase
{
    private static WorkloadDeployedDto BaseDto() => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId, WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "voest-app",
        ChartVersion = "1.2.3",
        ValuesYaml = string.Empty,
        Values = Array.Empty<ValueOverrideDto>(),
    };

    private string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    private string LockSecret(int revision) => $"sh.helm.release.v1.{Release}.v{revision}";

    [Test]
    public async Task StalePendingLock_IsClearedBeforePreFlight()
    {
        // Arrange — pending revision whose release secret is far older than the threshold.
        Helm.GetLatestReleaseRevisionAsync(Release, PoolNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(11, "pending-upgrade"));
        Gateway.GetSecretCreationTimestampAsync(PoolNamespace, LockSecret(11), Arg.Any<CancellationToken>())
            .Returns(DateTime.UtcNow - TimeSpan.FromHours(2));

        // Act
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        // Assert — the lock secret is deleted BEFORE the pre-flight dry-run runs.
        Received.InOrder(() =>
        {
            Gateway.DeleteSecretAsync(PoolNamespace, LockSecret(11), Arg.Any<CancellationToken>());
            Helm.UpgradeInstallDryRunAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
                PoolNamespace, Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task FreshPendingLock_IsLeftAlone()
    {
        // Arrange — pending revision younger than the threshold: could be a live helm run on
        // the outgoing pod of a rolling operator upgrade. Never rob a live run of its lock.
        Helm.GetLatestReleaseRevisionAsync(Release, PoolNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(4, "pending-install"));
        Gateway.GetSecretCreationTimestampAsync(PoolNamespace, LockSecret(4), Arg.Any<CancellationToken>())
            .Returns(DateTime.UtcNow - TimeSpan.FromMinutes(1));

        // Act
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        // Assert
        await Gateway.DidNotReceive()
            .DeleteSecretAsync(PoolNamespace, LockSecret(4), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HealthyRelease_NeverInspectsLockSecret()
    {
        // Arrange — newest revision deployed: nothing to recover.
        Helm.GetLatestReleaseRevisionAsync(Release, PoolNamespace, Arg.Any<CancellationToken>())
            .Returns(new HelmReleaseRevision(3, "deployed"));

        // Act
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        // Assert
        await Gateway.DidNotReceiveWithAnyArgs()
            .GetSecretCreationTimestampAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HistoryLookupFails_DeployStillRuns()
    {
        // Arrange — the stale-lock check is best effort and must never block a deploy.
        Helm.GetLatestReleaseRevisionAsync(Release, PoolNamespace, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("helm binary exploded"));

        // Act
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        // Assert — the real install still ran.
        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
            PoolNamespace, Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }
}
