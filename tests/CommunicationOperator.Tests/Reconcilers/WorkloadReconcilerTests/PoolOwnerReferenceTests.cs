using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

/// <summary>
///     AB#4924 §7.1 — owner references so a deleted lending tenant garbage-collects its pool. The
///     owner is the tenant's <c>CommunicationPool</c> CR, the only Kubernetes object that stands
///     for a tenant; deleting the tenant deletes the CR and the garbage collector takes the pool's
///     Deployments and its operator-owned Secret with it.
///
///     🔴 The refusal cases matter as much as the happy path. Kubernetes treats a namespaced
///     dependent whose owner lives in another namespace as having a missing owner and deletes it,
///     so writing a cross-namespace reference would destroy a live pool rather than fail to clean
///     up a dead one.
/// </summary>
internal class PoolOwnerReferenceTests : WorkloadReconcilerTestsBase
{
    private static readonly V1OwnerReference Owner = new()
    {
        ApiVersion = "octo-mesh.meshmakers.io/v1alpha1",
        Kind = "CommunicationPool",
        Name = CommunicationPoolManager.GetCrName(TenantId, PoolRtId),
        Uid = "5f0f2b6e-1b6a-4f55-9f0a-7c7b2f0b1234",
    };

    private static WorkloadDeployedDto Dto(WorkloadTypeDto workloadType, bool withSecretValue = false) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = workloadType,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "octo-mesh-adapter",
        ChartVersion = "1.2.3",
        Values = withSecretValue
            ? [new ValueOverrideDto { Path = "oauth.clientSecret", Value = "s3cr3t", IsSecret = true }]
            : [],
    };

    private void GivenTheTenantsCommunicationPoolCrExists() =>
        Gateway.TryGetCommunicationPoolOwnerReferenceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(Owner);

    private static string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    [Test]
    public async Task DeployAsync_AdapterPool_LooksUpTheOwnerByTheTenantsCommunicationPoolCrName()
    {
        GivenTheTenantsCommunicationPoolCrExists();

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Gateway.Received(1).TryGetCommunicationPoolOwnerReferenceAsync(
            PoolNamespace, CommunicationPoolManager.GetCrName(TenantId, PoolRtId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPool_StampsTheOwnerOntoTheReleasesDeployments()
    {
        GivenTheTenantsCommunicationPoolCrExists();
        Gateway.SetDeploymentOwnerReferenceByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>()).Returns(1);

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Gateway.Received(1).SetDeploymentOwnerReferenceByInstanceAsync(
            PoolNamespace, Release, Arg.Is<V1OwnerReference>(o => o.Uid == Owner.Uid),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPool_StampsTheOwnerOntoTheOperatorOwnedSecret()
    {
        GivenTheTenantsCommunicationPoolCrExists();

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool, withSecretValue: true), CancellationToken.None);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace,
            Arg.Is<V1Secret>(s => s.Metadata.OwnerReferences != null
                                  && s.Metadata.OwnerReferences.Count == 1
                                  && s.Metadata.OwnerReferences[0].Uid == Owner.Uid),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPool_OwnerIsWrittenOnlyAfterTheRealInstall()
    {
        // Before the install there are no Deployments to own — helm creates them. Stamping earlier
        // would silently patch nothing on a first install and look like it worked.
        GivenTheTenantsCommunicationPoolCrExists();
        Gateway.SetDeploymentOwnerReferenceByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>()).Returns(1);

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        Received.InOrder(() =>
        {
            Helm.UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(), PoolNamespace,
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>());
            Gateway.SetDeploymentOwnerReferenceByInstanceAsync(PoolNamespace, Release,
                Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    [Arguments(WorkloadTypeDto.Adapter)]
    [Arguments(WorkloadTypeDto.Application)]
    public async Task DeployAsync_TenantWorkload_NeverAsksForAnOwnerAndNeverStampsOne(WorkloadTypeDto workloadType)
    {
        await Reconciler.DeployAsync(Dto(workloadType, withSecretValue: true), CancellationToken.None);

        await Gateway.DidNotReceiveWithAnyArgs().TryGetCommunicationPoolOwnerReferenceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceiveWithAnyArgs().SetDeploymentOwnerReferenceByInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateSecretAsync(PoolNamespace,
            Arg.Is<V1Secret>(s => s.Metadata.OwnerReferences == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPoolInAForeignNamespace_RefusesToWriteACrossNamespaceOwner()
    {
        // The CR lives in PoolNamespace. An owner reference from another namespace is not merely
        // ineffective — the garbage collector deletes the dependent — so it is not written at all.
        Options.PlatformNamespace = "octo-platform";
        GivenTheTenantsCommunicationPoolCrExists();

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool, withSecretValue: true), CancellationToken.None);

        await Gateway.DidNotReceiveWithAnyArgs().TryGetCommunicationPoolOwnerReferenceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceiveWithAnyArgs().SetDeploymentOwnerReferenceByInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateSecretAsync("octo-platform",
            Arg.Is<V1Secret>(s => s.Metadata.OwnerReferences == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_AdapterPoolWithoutACommunicationPoolCr_DeploysWithoutAnOwner()
    {
        Gateway.TryGetCommunicationPoolOwnerReferenceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns((V1OwnerReference?)null);

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(), PoolNamespace,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
        await Gateway.DidNotReceiveWithAnyArgs().SetDeploymentOwnerReferenceByInstanceAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_OwnerLookupFails_DeployStillSucceeds()
    {
        // Garbage collection is a safety net behind the controller's undeploy cascade. Losing the
        // net is not a reason to refuse a pool that is otherwise deployable.
        Gateway.TryGetCommunicationPoolOwnerReferenceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("apiserver down"));

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(), PoolNamespace,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_OwnerStampFails_DeployStillSucceeds()
    {
        GivenTheTenantsCommunicationPoolCrExists();
        Gateway.SetDeploymentOwnerReferenceByInstanceAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<V1OwnerReference>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("forbidden"));

        await Reconciler.DeployAsync(Dto(WorkloadTypeDto.AdapterPool), CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(Release, Arg.Any<string>(), Arg.Any<string>(), PoolNamespace,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}
