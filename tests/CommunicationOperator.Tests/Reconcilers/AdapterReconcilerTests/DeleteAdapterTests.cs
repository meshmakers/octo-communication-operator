using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.AdapterReconcilerTests;

public class DeleteAdapterTests : AdapterReconcilerTestsBase
{
    [Test]
    public async Task DeleteAsync_ExistingDeploymentAndService_BothDeleted()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();
        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Deployment> { ExistingDeployment("adapter-dep") });
        KubernetesClient.ListAsync<V1Service>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Service> { ExistingService("adapter-svc") });

        await Reconciler.DeleteAsync(pool, adapterDto);

        await KubernetesClient.Received(1).DeleteAsync<V1Deployment>("adapter-dep", Namespace);
        await KubernetesClient.Received(1).DeleteAsync<V1Service>("adapter-svc", Namespace);
    }

    [Test]
    public async Task DeleteAsync_NotifiesPoolHubWithDeployedFalse()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.DeleteAsync(pool, adapterDto);

        await PoolHubClient.Received(1)
            .UpdateAdapterDeploymentStateAsync(PoolName, adapterDto.AdapterRtEntityId, false);
    }

    [Test]
    public async Task DeleteAsync_KubernetesThrows_WrappedAsAdapterReconsilerExceptionAndHubNotNotified()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();
        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("k8s api down"));

        await Assert.That(async () => await Reconciler.DeleteAsync(pool, adapterDto))
            .Throws<AdapterReconsilerException>();

        await PoolHubClient.DidNotReceive()
            .UpdateAdapterDeploymentStateAsync(Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>(), Arg.Any<bool>());
    }
}
