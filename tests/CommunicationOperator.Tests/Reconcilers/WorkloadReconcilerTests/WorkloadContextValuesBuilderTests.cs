using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

// All assertions use the double-quoted form because the builder runs every string
// scalar — keys included — through QuotedStringEventEmitter. The quoting is what
// stops Go YAML (used by Helm) from coercing all-digit strings like the
// blueprint-seeded RtId 670000000000000000000002 into float64 ("6.7e+23"), which
// otherwise lands in the rendered Deployment env and crashes the adapter SDK with
// "not a valid 24 digit hex string". Bools / numbers pass through unquoted —
// quoting tls: true would change chart-side branching semantics.
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
        await Assert.That(yaml!).Contains("\"instancePrefix\": \"test-2\"");
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
        await Assert.That(yaml!).Contains("\"clusterDependencies\":");
        await Assert.That(yaml!).Contains("\"mongodbHost\":");
        await Assert.That(yaml!).Contains("\"rabbitMqHost\": \"rabbitmq.rabbitmq.svc.cluster.local\"");
        await Assert.That(yaml!).Contains("\"rabbitMqUser\": \"octo-mq-user\"");
        await Assert.That(yaml!).Contains("\"streamDataHost\":");
        await Assert.That(yaml!).Contains("\"streamDataUser\": \"octo-system\"");
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
        await Assert.That(yaml!).Contains("\"mongodbHost\": \"host-only\"");
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
        await Assert.That(yaml!).Contains("\"ingress\":");
        await Assert.That(yaml!).Contains("\"className\": \"nginx\"");
        // Bool intentionally NOT quoted so the chart's {{ if .Values.ingress.tls }}
        // branch still evaluates the bool, not the string "true".
        await Assert.That(yaml!).Contains("\"tls\": true");
        await Assert.That(yaml!).Contains("\"cert-manager.io/cluster-issuer\": \"mm-cloud-issuer\"");
    }

    [Test]
    public async Task Build_IngressExtraAnnotations_MergedWithClusterIssuer()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            Ingress = new IngressDefaultsOptions
            {
                ClusterIssuer = "mm-cloud-issuer",
                Annotations =
                [
                    new IngressAnnotationOption
                    {
                        Name = "nginx.ingress.kubernetes.io/proxy-body-size",
                        Value = "25m",
                    },
                    new IngressAnnotationOption { Name = "  ", Value = "ignored" },
                    new IngressAnnotationOption { Name = "no-value", Value = null },
                ],
            },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"cert-manager.io/cluster-issuer\": \"mm-cloud-issuer\"");
        await Assert.That(yaml!).Contains("\"nginx.ingress.kubernetes.io/proxy-body-size\": \"25m\"");
        await Assert.That(yaml!).DoesNotContain("ignored");
        await Assert.That(yaml!).DoesNotContain("no-value");
    }

    [Test]
    public async Task Build_IngressAnnotationsWithoutClusterIssuer_EmitsAnnotations()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            Ingress = new IngressDefaultsOptions
            {
                Annotations =
                [
                    new IngressAnnotationOption
                    {
                        Name = "nginx.ingress.kubernetes.io/proxy-body-size",
                        Value = "25m",
                    },
                ],
            },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"nginx.ingress.kubernetes.io/proxy-body-size\": \"25m\"");
        await Assert.That(yaml!).DoesNotContain("cert-manager.io/cluster-issuer");
    }

    [Test]
    public async Task Build_NullTls_DoesNotEmitTlsKey()
    {
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions
        {
            Ingress = new IngressDefaultsOptions { ClassName = "nginx" },
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"className\": \"nginx\"");
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
        await Assert.That(yaml!).Contains("\"tenantId\": \"meshtest\"");
        await Assert.That(yaml!).Contains("\"adapterRtId\": \"5f1c4e1a4d3b2a1b8f9c1234\"");
    }

    [Test]
    public async Task Build_AllDigitRtId_IsQuotedToPreventFloatCoercion()
    {
        // Regression test: the System.Communication blueprint seeds adapter
        // RtIds like 670000000000000000000002 (24 decimal digits). Without
        // forced quoting, YamlDotNet emits a plain scalar that Go YAML (used
        // by Helm) parses as float64 → "6.7e+23". That value then flows into
        // OCTO_ADAPTER__ADAPTERRTID in the rendered Deployment, and the SDK
        // throws FormatException("not a valid 24 digit hex string") on every
        // SignalR reconnect — the adapter never reaches CommunicationState
        // Online.
        var yaml = WorkloadContextValuesBuilder.Build(new OperatorOptions(), new WorkloadDeployedDto
        {
            TenantId = "meshtest",
            WorkloadRtId = "670000000000000000000002",
            WorkloadName = "mesh-adapter",
        });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"adapterRtId\": \"670000000000000000000002\"");
        await Assert.That(yaml!).DoesNotContain("6.7e+23");
        await Assert.That(yaml!).DoesNotContain("6.7E+23");
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
        await Assert.That(yaml!).Contains("\"image\":");
        await Assert.That(yaml!).Contains("\"privateRegistry\": \"docker.mm.cloud\"");
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
        await Assert.That(yaml!).Contains("\"communicationControllerServiceUri\": \"http://octo-communication.octo.svc.cluster.local\"");
        await Assert.That(yaml!).Contains("\"reportingServiceUri\": \"http://octo-reporting.octo.svc.cluster.local\"");
    }

    [Test]
    public async Task Build_WorkloadIngressEnabledWithHostname_EmitsEnabledAndPublicUri()
    {
        // Happy path: workload opts in + supplies a hostname. Operator emits
        // ingress.enabled=true (so the chart actually renders the Ingress
        // template) plus publicUri at the top level (the chart's host rule
        // strips the https:// prefix itself — see
        // octo-mesh-adapter/templates/ingress.yaml). Cluster-wide ingress
        // defaults still stack on top.
        var yaml = WorkloadContextValuesBuilder.Build(
            new OperatorOptions
            {
                Ingress = new IngressDefaultsOptions
                {
                    ClassName = "nginx",
                    ClusterIssuer = "mm-cloud-issuer",
                    Tls = true,
                },
            },
            new WorkloadDeployedDto
            {
                TenantId = "meshtest",
                WorkloadRtId = "5f1c4e1a4d3b2a1b8f9c1234",
                WorkloadName = "mesh-adapter",
                IngressEnabled = true,
                Hostname = "adapter.staging.octo-mesh.com",
            });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"enabled\": true");
        await Assert.That(yaml!).Contains("\"publicUri\": \"https://adapter.staging.octo-mesh.com\"");
        await Assert.That(yaml!).Contains("\"className\": \"nginx\"");
        await Assert.That(yaml!).Contains("\"cert-manager.io/cluster-issuer\": \"mm-cloud-issuer\"");
    }

    [Test]
    public async Task Build_WorkloadIngressDisabled_OmitsEnabledAndPublicUri()
    {
        // Default state: workload did not opt in. Cluster-wide ingress defaults
        // (className, cluster-issuer) may still be emitted but ingress.enabled
        // stays absent so the chart's own values.yaml default (enabled=false)
        // wins and no Ingress is rendered.
        var yaml = WorkloadContextValuesBuilder.Build(
            new OperatorOptions
            {
                Ingress = new IngressDefaultsOptions { ClassName = "nginx" },
            },
            new WorkloadDeployedDto
            {
                TenantId = "meshtest",
                WorkloadRtId = "5f1c4e1a4d3b2a1b8f9c1234",
                WorkloadName = "mesh-adapter",
                IngressEnabled = false,
                Hostname = "adapter.staging.octo-mesh.com",
            });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).DoesNotContain("\"enabled\":");
        await Assert.That(yaml!).DoesNotContain("publicUri");
        await Assert.That(yaml!).Contains("\"className\": \"nginx\"");
    }

    [Test]
    public async Task Build_WorkloadIngressEnabledNoHostname_DefensivelyOmitsEnabledAndPublicUri()
    {
        // Defensive branch: controller-side validation should reject this
        // combination at Deploy time, but if an inconsistent DTO ever reaches
        // the operator we still refuse to render an Ingress with an empty host
        // (k8s admission would reject it mid-helm-upgrade and leave the release
        // in a failed state). publicUri is also omitted so the chart doesn't
        // see an https:// fragment with nothing after it.
        var yaml = WorkloadContextValuesBuilder.Build(
            new OperatorOptions { Ingress = new IngressDefaultsOptions { ClassName = "nginx" } },
            new WorkloadDeployedDto
            {
                TenantId = "meshtest",
                WorkloadRtId = "5f1c4e1a4d3b2a1b8f9c1234",
                WorkloadName = "mesh-adapter",
                IngressEnabled = true,
                Hostname = null,
            });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).DoesNotContain("\"enabled\":");
        await Assert.That(yaml!).DoesNotContain("publicUri");
    }

    [Test]
    public async Task Build_WorkloadIngressEnabledWithHostnameAndNoOperatorDefaults_StillEmitsEnabledAndPublicUri()
    {
        // Cluster has no ingress defaults configured (className, cluster-issuer
        // and TLS all unset) — per-workload opt-in still works. The chart then
        // falls back on its own values.yaml defaults for className/TLS and the
        // ingress just renders without a cert-manager annotation.
        var yaml = WorkloadContextValuesBuilder.Build(
            new OperatorOptions(),
            new WorkloadDeployedDto
            {
                TenantId = "meshtest",
                WorkloadRtId = "5f1c4e1a4d3b2a1b8f9c1234",
                WorkloadName = "mesh-adapter",
                IngressEnabled = true,
                Hostname = "adapter.staging.octo-mesh.com",
            });

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"enabled\": true");
        await Assert.That(yaml!).Contains("\"publicUri\": \"https://adapter.staging.octo-mesh.com\"");
        await Assert.That(yaml!).DoesNotContain("className");
        await Assert.That(yaml!).DoesNotContain("cluster-issuer");
    }
}
