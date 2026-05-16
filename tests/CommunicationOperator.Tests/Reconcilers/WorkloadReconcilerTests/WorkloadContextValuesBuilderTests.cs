using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class WorkloadContextValuesBuilderTests
{
    [Test]
    public async Task Build_EmptyOptions_ReturnsNull()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions());
        await Assert.That(yaml).IsNull();
    }

    [Test]
    public async Task Build_OnlyInstancePrefix_EmitsTopLevelKey()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            InstancePrefix = "test-2",
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("instancePrefix: test-2");
        await Assert.That(yaml!).DoesNotContain("clusterDependencies");
        await Assert.That(yaml!).DoesNotContain("ingress");
    }

    [Test]
    public async Task Build_FullClusterDependencies_EmitsNestedMap()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            ClusterDependencies = new ClusterDependenciesOptions
            {
                MongodbHost = "octo-mongodb-0.octo-mongodb-svc.mongodb.svc.cluster.local:27017",
                RabbitMqHost = "rabbitmq.rabbitmq.svc.cluster.local",
                RabbitMqUser = "octo-mq-user",
                StreamDataHost = "crate-octo-crate.cratedb.svc.cluster.local",
                StreamDataUser = "octo-system",
            },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("clusterDependencies:");
        await Assert.That(yaml!).Contains("mongodbHost:");
        await Assert.That(yaml!).Contains("rabbitMqHost: rabbitmq.rabbitmq.svc.cluster.local");
        await Assert.That(yaml!).Contains("rabbitMqUser: octo-mq-user");
        await Assert.That(yaml!).Contains("streamDataHost:");
        await Assert.That(yaml!).Contains("streamDataUser: octo-system");
    }

    [Test]
    public async Task Build_PartialClusterDependencies_OnlyEmitsSetKeys()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            ClusterDependencies = new ClusterDependenciesOptions
            {
                MongodbHost = "host-only",
                // others left null
            },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("mongodbHost: host-only");
        await Assert.That(yaml!).DoesNotContain("rabbitMqHost");
        await Assert.That(yaml!).DoesNotContain("streamDataHost");
    }

    [Test]
    public async Task Build_IngressDefaults_BuildsAnnotationsAndFlags()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            Ingress = new IngressDefaultsOptions
            {
                ClassName = "nginx",
                ClusterIssuer = "mm-cloud-issuer",
                Tls = true,
            },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("ingress:");
        await Assert.That(yaml!).Contains("className: nginx");
        await Assert.That(yaml!).Contains("tls: true");
        await Assert.That(yaml!).Contains("cert-manager.io/cluster-issuer: mm-cloud-issuer");
    }

    [Test]
    public async Task Build_NullTls_DoesNotEmitTlsKey()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            Ingress = new IngressDefaultsOptions { ClassName = "nginx" },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("className: nginx");
        await Assert.That(yaml!).DoesNotContain("tls:");
    }

    [Test]
    public async Task Build_WorkloadIdentity_EmitsTenantIdAndAdapterRtId()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions(), new WorkloadDeployedDto
        {
            TenantId = "meshtest",
            WorkloadRtId = "5f1c4e1a4d3b2a1b8f9c1234",
            WorkloadName = "mesh-adapter",
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("tenantId: meshtest");
        await Assert.That(yaml!).Contains("adapterRtId: 5f1c4e1a4d3b2a1b8f9c1234");
    }

    [Test]
    public async Task Build_NullWorkload_OmitsIdentityKeys()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions { InstancePrefix = "test-2" });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).DoesNotContain("tenantId");
        await Assert.That(yaml!).DoesNotContain("adapterRtId");
    }

    [Test]
    public async Task Build_ImageRegistry_EmitsImagePrivateRegistry()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            ImageRegistry = "docker.mm.cloud",
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("image:");
        await Assert.That(yaml!).Contains("privateRegistry: docker.mm.cloud");
    }

    [Test]
    public async Task Build_ControllerAndReportingUris_EmitsTopLevelKeys()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            CommunicationControllerUri = "http://octo-communication.octo.svc.cluster.local",
            ReportingServiceUri = "http://octo-reporting.octo.svc.cluster.local",
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("communicationControllerServiceUri: http://octo-communication.octo.svc.cluster.local");
        await Assert.That(yaml!).Contains("reportingServiceUri: http://octo-reporting.octo.svc.cluster.local");
    }
}
