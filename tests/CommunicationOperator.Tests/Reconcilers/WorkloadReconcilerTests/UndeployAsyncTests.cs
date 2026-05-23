using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class UndeployAsyncTests : WorkloadReconcilerTestsBase
{
    private static WorkloadUndeployedDto BaseDto() => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId, PoolName = PoolName,
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
}
