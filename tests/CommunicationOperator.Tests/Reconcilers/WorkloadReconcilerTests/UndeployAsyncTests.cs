using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class UndeployAsyncTests : WorkloadReconcilerTestsBase
{
    private static WorkloadUndeployedDto BaseDto() => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId, WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Application,
    };

    [Test]
    public async Task UndeployAsync_CallsHelmUninstallWithReleaseAndNamespace()
    {
        var expectedRelease = WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

        await Reconciler.UndeployAsync(BaseDto(), CancellationToken.None);

        await Helm.Received(1).UninstallAsync(
            Arg.Is<string>(r => r == expectedRelease),
            Arg.Is<string>(n => n == PoolNamespace),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UndeployAsync_SecretExists_DeletesIt()
    {
        Gateway.SecretExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Reconciler.UndeployAsync(BaseDto(), CancellationToken.None);

        await Gateway.Received(1).DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UndeployAsync_SecretDoesNotExist_NoDeleteAttempted()
    {
        Gateway.SecretExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await Reconciler.UndeployAsync(BaseDto(), CancellationToken.None);

        await Gateway.DidNotReceiveWithAnyArgs()
            .DeleteSecretAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UndeployAsync_DeployInFlight_CancelsItBeforeUninstall()
    {
        // Simulate a stuck deploy by holding helm.UpgradeInstallAsync until
        // the test signals — the deploy will sit in _inFlightDeploys with
        // its CancellationTokenSource published. The concurrent UndeployAsync
        // must reach in, cancel the deploy, then run uninstall.

        var deployDto = new WorkloadDeployedDto
        {
            TenantId = TenantId,
            PoolRtId = PoolRtId,
            WorkloadRtId = WorkloadRtId,
            WorkloadName = WorkloadName,
            WorkloadType = WorkloadTypeDto.Application,
            RepositoryUrl = "https://example.invalid",
            ChartName = "voest-app",
            ChartVersion = "1.0.0",
            ValuesYaml = string.Empty,
            Values = Array.Empty<ValueOverrideDto>(),
        };

        CancellationToken capturedDeployToken = default;
        var helmStarted = new TaskCompletionSource();
        Helm.UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                capturedDeployToken = call.Arg<CancellationToken>();
                helmStarted.TrySetResult();
                // Wait until cancelled — that's what a stuck `helm install`
                // looks like to the reconciler (in production
                // HelmProcessInvoker propagates OperationCanceledException
                // after Process.Kill).
                await Task.Delay(TimeSpan.FromSeconds(30), capturedDeployToken);
            });

        var deployTask = Reconciler.DeployAsync(deployDto, CancellationToken.None);

        // Wait until helm started so the in-flight CTS is registered.
        await helmStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var undeployTask = Reconciler.UndeployAsync(BaseDto(), CancellationToken.None);

        // The deploy should throw OperationCanceledException as a result of
        // the undeploy's Cancel() call. The reconciler wraps Helm exceptions
        // in HelmException but OperationCanceledException propagates through
        // the catch (HelmException) without diagnostics enrichment.
        try { await deployTask; } catch (OperationCanceledException) { /* expected */ }

        await undeployTask;

        await Helm.Received(1).UninstallAsync(
            Arg.Is<string>(r => r == WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId)),
            Arg.Is<string>(n => n == PoolNamespace),
            Arg.Any<CancellationToken>());

        // The CTS captured by helm must have been cancelled — that's the
        // proof that UndeployAsync went through the cancel-first branch
        // rather than serializing behind the install.
        await Assert.That(capturedDeployToken.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task UndeployAsync_NoDeployInFlight_BehavesAsBefore()
    {
        // Sanity check that the new cancel-first branch is skipped when no
        // deploy is registered — the existing path must keep working.
        await Reconciler.UndeployAsync(BaseDto(), CancellationToken.None);

        await Helm.Received(1).UninstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
