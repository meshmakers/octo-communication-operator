using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.AdapterReconcilerTests;

public class DeletePoolTests : AdapterReconcilerTestsBase
{
    private static K8Pool NewK8Pool() => new()
    {
        Namespace = Namespace,
        PoolName = PoolName,
        TenantId = TenantId
    };

    [Test]
    public async Task DeleteAsync_NoExistingResources_DoesNotCallDelete()
    {
        await Reconciler.DeleteAsync(NewK8Pool());

        await KubernetesClient.DidNotReceive()
            .DeleteAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>());
        await KubernetesClient.DidNotReceive()
            .DeleteAsync<V1Service>(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeleteAsync_ExistingDeployments_DeletesEachByName()
    {
        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Deployment> { ExistingDeployment("dep-a"), ExistingDeployment("dep-b") });

        await Reconciler.DeleteAsync(NewK8Pool());

        await KubernetesClient.Received(1).DeleteAsync<V1Deployment>("dep-a", Namespace);
        await KubernetesClient.Received(1).DeleteAsync<V1Deployment>("dep-b", Namespace);
    }

    [Test]
    public async Task DeleteAsync_ExistingServices_DeletesEachByName()
    {
        KubernetesClient.ListAsync<V1Service>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Service> { ExistingService("svc-a") });

        await Reconciler.DeleteAsync(NewK8Pool());

        await KubernetesClient.Received(1).DeleteAsync<V1Service>("svc-a", Namespace);
    }

    [Test]
    public async Task DeleteAsync_ListsByPoolLabels()
    {
        await Reconciler.DeleteAsync(NewK8Pool());

        await KubernetesClient.Received(1).ListAsync<V1Deployment>(
            Namespace,
            Arg.Is<string>(s =>
                s.Contains($"octo-mesh.meshmakers.io/pool={PoolName}") &&
                s.Contains($"octo-mesh.meshmakers.io/tenant={TenantId}")));
    }

    [Test]
    public async Task DeleteAsync_KubernetesThrows_WrappedAsAdapterReconsilerException()
    {
        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("k8s api down"));

        await Assert.That(async () => await Reconciler.DeleteAsync(NewK8Pool()))
            .Throws<AdapterReconsilerException>();
    }
}
