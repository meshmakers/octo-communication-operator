using k8s.Models;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.AdapterReconcilerTests;

public abstract class AdapterReconcilerTestsBase
{
    protected const string TenantId = "acme";
    protected const string PoolName = "default";
    protected const string Namespace = "octo";
    protected const string ControllerUri = "https://controller";

    protected readonly IKubernetesClient KubernetesClient;
    protected readonly IPoolHubClient PoolHubClient;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly AdapterReconciler Reconciler;

    protected AdapterReconcilerTestsBase()
    {
        KubernetesClient = Substitute.For<IKubernetesClient>();
        PoolHubClient = Substitute.For<IPoolHubClient>();
        OperatorOptions = new OperatorOptions();

        Reconciler = new AdapterReconciler(
            KubernetesClient,
            NullLogger<AdapterReconciler>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions));

        KubernetesClient.ListAsync<V1Deployment>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Deployment>());
        KubernetesClient.ListAsync<V1Service>(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new List<V1Service>());
    }

    protected const string AdapterRtId = "507f1f77bcf86cd799439011";

    protected static PoolCommunicationAdapterDto CreateAdapterDto(string rtId = AdapterRtId) =>
        new()
        {
            PoolName = PoolName,
            AdapterRtEntityId = new RtEntityId($"system-comm/RtAdapter@{rtId}"),
            ImageName = "octo/adapter",
            Version = "1.0.0"
        };

    protected Pool CreatePool() =>
        new(new PoolDescriptor
        {
            Namespace = Namespace,
            TenantId = TenantId,
            PoolName = PoolName,
            ControllerUri = ControllerUri,
            BrokerHost = "rabbit",
            BrokerVirtualHost = "/",
            BrokerPort = 5672,
            InstancePrefix = "instance"
        }, PoolHubClient, new V1CommunicationPoolEntity
        {
            Metadata = new V1ObjectMeta { Name = $"{TenantId}-{PoolName}", NamespaceProperty = Namespace }
        });

    protected static V1Deployment ExistingDeployment(string name) =>
        new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Namespace } };

    protected static V1Service ExistingService(string name) =>
        new() { Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Namespace } };
}
