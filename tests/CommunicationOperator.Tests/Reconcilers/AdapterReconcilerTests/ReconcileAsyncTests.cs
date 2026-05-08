using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.AdapterReconcilerTests;

public class ReconcileAsyncTests : AdapterReconcilerTestsBase
{
    [Test]
    public async Task ReconcileAsync_NoExistingResources_CreatesDeploymentAndService()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await KubernetesClient.Received(1)
            .CreateAsync(Arg.Is<V1Deployment>(d => d.Metadata.NamespaceProperty == Namespace));
        await KubernetesClient.Received(1)
            .CreateAsync(Arg.Is<V1Service>(s => s.Metadata.NamespaceProperty == Namespace));
    }

    [Test]
    public async Task ReconcileAsync_ExistingDeployment_DeletedAndRecreated()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();
        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Deployment> { ExistingDeployment("old-dep") });

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await KubernetesClient.Received(1).DeleteAsync<V1Deployment>("old-dep", Namespace);
        await KubernetesClient.Received(1).CreateAsync(Arg.Any<V1Deployment>());
    }

    [Test]
    public async Task ReconcileAsync_NotifiesPoolHubWithDeployedTrue()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await PoolHubClient.Received(1)
            .UpdateAdapterDeploymentStateAsync(PoolName, adapterDto.AdapterRtEntityId, true);
    }

    [Test]
    public async Task ReconcileAsync_NoImagePullSecretConfigured_DeploymentHasNoImagePullSecrets()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await KubernetesClient.Received(1)
            .CreateAsync(Arg.Is<V1Deployment>(d => d.Spec.Template.Spec.ImagePullSecrets == null));
    }

    [Test]
    public async Task ReconcileAsync_ImagePullSecretConfigured_DeploymentReferencesIt()
    {
        OperatorOptions.ImagePullSecretName = "registry-pull-secret";
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await KubernetesClient.Received(1).CreateAsync(Arg.Is<V1Deployment>(d =>
            d.Spec.Template.Spec.ImagePullSecrets != null &&
            d.Spec.Template.Spec.ImagePullSecrets.Count == 1 &&
            d.Spec.Template.Spec.ImagePullSecrets[0].Name == "registry-pull-secret"));
    }

    [Test]
    public async Task ReconcileAsync_DeploymentEnvCarriesPoolDescriptorAndAdapterIdentity()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();

        await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity);

        await KubernetesClient.Received(1).CreateAsync(Arg.Is<V1Deployment>(d =>
            d.Spec.Template.Spec.Containers[0].Env.Any(e => e.Name == "OCTO_ADAPTER__TENANTID" && e.Value == TenantId) &&
            d.Spec.Template.Spec.Containers[0].Env.Any(e => e.Name == "OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI" && e.Value == ControllerUri) &&
            d.Spec.Template.Spec.Containers[0].Env.Any(e => e.Name == "OCTO_ADAPTER__BROKERHOST" && e.Value == "rabbit") &&
            d.Spec.Template.Spec.Containers[0].Env.Any(e => e.Name == "OCTO_ADAPTER__ADAPTERRTID" && e.Value == adapterDto.AdapterRtEntityId.RtId.ToString())));
    }

    [Test]
    public async Task ReconcileAsync_KubernetesThrows_WrappedAsAdapterReconsilerExceptionAndHubNotNotified()
    {
        var pool = CreatePool();
        var adapterDto = CreateAdapterDto();
        KubernetesClient.CreateAsync(Arg.Any<V1Deployment>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("k8s api down"));

        await Assert.That(async () => await Reconciler.ReconcileAsync(pool, adapterDto, pool.Entity))
            .Throws<AdapterReconsilerException>();

        await PoolHubClient.DidNotReceive()
            .UpdateAdapterDeploymentStateAsync(Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>(), Arg.Any<bool>());
    }
}
