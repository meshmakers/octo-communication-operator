using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class ScaleAsyncTests : WorkloadReconcilerTestsBase
{
    private static ScaleWorkloadDto Dto(int replicas) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Adapter,
        Replicas = replicas,
    };

    [Test]
    public async Task ScaleAsync_PatchesDeploymentsInPoolNamespaceWithReleaseNameAndReplicas()
    {
        Gateway.ScaleDeploymentsByInstanceAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await Reconciler.ScaleAsync(Dto(replicas: 0), CancellationToken.None);

        var expectedRelease = WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);
        await Gateway.Received(1).ScaleDeploymentsByInstanceAsync(
            PoolNamespace, expectedRelease, 0, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScaleAsync_ReturnsGatewayPatchCount()
    {
        Gateway.ScaleDeploymentsByInstanceAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var patched = await Reconciler.ScaleAsync(Dto(replicas: 1), CancellationToken.None);

        await Assert.That(patched).IsEqualTo(2);
    }

    [Test]
    public async Task ScaleAsync_NoDeploymentsFound_ReturnsZeroWithoutThrowing()
    {
        // Default substitute behavior: gateway returns 0 — the release has no
        // Deployments (e.g. never deployed, or already undeployed). The
        // reconciler logs a warning and reports 0; the caller decides how to
        // surface it (OperatorHubService maps 0 to a failed scale ack).
        var patched = await Reconciler.ScaleAsync(Dto(replicas: 1), CancellationToken.None);

        await Assert.That(patched).IsEqualTo(0);
    }
}
