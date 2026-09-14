using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#4924 §7.1 — an <see cref="WorkloadTypeDto.AdapterPool"/> is deployed into the platform
///     namespace instead of the namespace tenant workloads go to, because its members run work for
///     tenants other than the one that owns it (concept §4b, Q1).
///
///     🔴 The second half of these tests is the point of the first half. Relaxing "one namespace for
///     everything the operator deploys" is the whole risk of this increment, so the same three verbs
///     are pinned for <see cref="WorkloadTypeDto.Adapter"/> and <see cref="WorkloadTypeDto.Application"/>
///     with a platform namespace configured: those must keep landing in
///     <c>PoolNamespace</c>, whatever the new option says. A relaxation that reaches one notch too
///     far shows up here and nowhere else.
/// </summary>
internal class PlatformNamespaceTests : WorkloadReconcilerTestsBase
{
    private const string PlatformNamespace = "octo-platform";

    private static WorkloadDeployedDto DeployDto(WorkloadTypeDto workloadType) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = workloadType,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "octo-mesh-adapter",
        ChartVersion = "1.2.3",
    };

    private static WorkloadUndeployedDto UndeployDto(WorkloadTypeDto workloadType) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = workloadType,
    };

    private static ScaleWorkloadDto ScaleDto(WorkloadTypeDto workloadType, int replicas) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = workloadType,
        Replicas = replicas,
    };

    private static string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    [Test]
    public async Task DeployAsync_AdapterPool_InstallsIntoThePlatformNamespace()
    {
        Options.PlatformNamespace = PlatformNamespace;

        await Reconciler.DeployAsync(DeployDto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
            PlatformNamespace, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
        await Helm.Received(1).UpgradeInstallDryRunAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
            PlatformNamespace, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPool_WithoutAConfiguredPlatformNamespace_StaysInThePoolNamespace()
    {
        // Unset is the default and resolves to the pool namespace, which is a platform namespace
        // already — and the only one in which the tenant's CommunicationPool CR can own the pool
        // (see OperatorOptions.PlatformNamespace).
        await Reconciler.DeployAsync(DeployDto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
            PoolNamespace, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UndeployAsync_AdapterPool_UninstallsFromThePlatformNamespace()
    {
        Options.PlatformNamespace = PlatformNamespace;

        await Reconciler.UndeployAsync(UndeployDto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UninstallAsync(Release, PlatformNamespace, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScaleAsync_AdapterPool_PatchesDeploymentsInThePlatformNamespace()
    {
        Options.PlatformNamespace = PlatformNamespace;
        Gateway.ScaleDeploymentsByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(1);

        await Reconciler.ScaleAsync(ScaleDto(WorkloadTypeDto.AdapterPool, replicas: 3), CancellationToken.None);

        await Gateway.Received(1).ScaleDeploymentsByInstanceAsync(PlatformNamespace, Release, 3,
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(WorkloadTypeDto.Adapter)]
    [Arguments(WorkloadTypeDto.Application)]
    public async Task DeployAsync_TenantWorkload_StaysInThePoolNamespaceEvenWithAPlatformNamespaceConfigured(
        WorkloadTypeDto workloadType)
    {
        Options.PlatformNamespace = PlatformNamespace;

        await Reconciler.DeployAsync(DeployDto(workloadType), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(),
            PoolNamespace, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
        await Helm.DidNotReceive().UpgradeInstallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            PlatformNamespace, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(WorkloadTypeDto.Adapter)]
    [Arguments(WorkloadTypeDto.Application)]
    public async Task UndeployAsync_TenantWorkload_StaysInThePoolNamespaceEvenWithAPlatformNamespaceConfigured(
        WorkloadTypeDto workloadType)
    {
        Options.PlatformNamespace = PlatformNamespace;

        await Reconciler.UndeployAsync(UndeployDto(workloadType), CancellationToken.None);

        await Helm.Received(1).UninstallAsync(Release, PoolNamespace, Arg.Any<CancellationToken>());
        await Helm.DidNotReceive().UninstallAsync(Arg.Any<string>(), PlatformNamespace, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(WorkloadTypeDto.Adapter)]
    [Arguments(WorkloadTypeDto.Application)]
    public async Task ScaleAsync_TenantWorkload_StaysInThePoolNamespaceEvenWithAPlatformNamespaceConfigured(
        WorkloadTypeDto workloadType)
    {
        Options.PlatformNamespace = PlatformNamespace;
        Gateway.ScaleDeploymentsByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(1);

        await Reconciler.ScaleAsync(ScaleDto(workloadType, replicas: 0), CancellationToken.None);

        await Gateway.Received(1).ScaleDeploymentsByInstanceAsync(PoolNamespace, Release, 0,
            Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().ScaleDeploymentsByInstanceAsync(PlatformNamespace, Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(WorkloadTypeDto.Adapter)]
    [Arguments(WorkloadTypeDto.Application)]
    public async Task DeployAsync_TenantWorkload_WritesItsSecretIntoThePoolNamespace(WorkloadTypeDto workloadType)
    {
        Options.PlatformNamespace = PlatformNamespace;
        var dto = DeployDto(workloadType) with
        {
            Values = [new ValueOverrideDto { Path = "oauth.clientSecret", Value = "s3cr3t", IsSecret = true }],
        };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Any<k8s.Models.V1Secret>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPool_WritesItsSecretIntoThePlatformNamespace()
    {
        Options.PlatformNamespace = PlatformNamespace;
        var dto = DeployDto(WorkloadTypeDto.AdapterPool) with
        {
            Values = [new ValueOverrideDto { Path = "oauth.clientSecret", Value = "s3cr3t", IsSecret = true }],
        };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Gateway.Received(1).CreateSecretAsync(PlatformNamespace, Arg.Any<k8s.Models.V1Secret>(),
            Arg.Any<CancellationToken>());
    }
}
