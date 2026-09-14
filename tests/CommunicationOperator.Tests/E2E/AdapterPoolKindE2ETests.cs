using System.Text.Json.Serialization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TUnit.Core;

namespace Meshmakers.Octo.Communication.Operator.Tests.E2E;

/// <summary>
///     AB#4924 §7.3 — end-to-end against a real Kubernetes apiserver: an adapter pool is one
///     workload with a replica range, scaled 1 → 3 → 1 through the AB#4917 scale verb, and owned by
///     its lending tenant's <c>CommunicationPool</c> CR so that deleting the tenant garbage-collects
///     the pool.
///
///     <para>
///     The two things these tests prove cannot be proved with a substitute. Kubernetes' garbage
///     collector is a real controller with real rules — in particular the rule that a dependent
///     whose owner sits in another namespace is <i>deleted</i> rather than merely unowned, which is
///     why the operator refuses to write such a reference at all. And a merge patch against the
///     Deployments of a release either moves <c>spec.replicas</c> on the live object or it does not.
///     </para>
///
///     <para>
///     Helm is deliberately not in the loop: nothing in this increment changed the helm layer
///     (<c>HelmRunnerTests</c> owns its argument construction), and standing in for it with a
///     directly-created Deployment carrying the release's <c>app.kubernetes.io/instance</c> label
///     is exactly the shape the scale path selects on.
///     </para>
///
///     <para>
///     <b>To run:</b>
///     <code>
///     OCTO_OPERATOR_E2E_KUBECONTEXT=kind-kind \
///       dotnet test --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj \
///       -c DebugL --filter "/*/*/AdapterPoolKindE2ETests/*"
///     </code>
///     Without the environment variable every test in this class reports as <b>skipped</b>, never as
///     passed — a green run on a machine with no cluster would be a lie about what was verified.
///     </para>
/// </summary>
internal class AdapterPoolKindE2ETests
{
    private const string EnvironmentVariable = "OCTO_OPERATOR_E2E_KUBECONTEXT";
    private const string Namespace = "octo-pool-e2e";
    private const string TenantId = "e2etenant";
    private const string PoolRtId = "65d5c447b420da3fb1230e2e";
    private const string WorkloadRtId = "65d5c447b420da3fb1231e2e";
    private const string WorkloadName = "e2e-adapter-pool";

    // Present on every kind node, so the ReplicaSet controller can create pods without a registry
    // round trip. The pods never have to become Ready — spec.replicas and status.replicas are what
    // the scale verb moves, and both are set by the apiserver and the ReplicaSet controller.
    private const string PauseImage = "registry.k8s.io/pause:3.10";

    private static string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    private static string CrName => CommunicationPoolManager.GetCrName(TenantId, PoolRtId);

    private static async Task<(IKubernetes Client, WorkloadReconciler Reconciler,
        CommunicationPoolKubernetesGateway Gateway, OperatorOptions Options)> ArrangeAsync()
    {
        var context = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(context))
        {
            Skip.Test(
                $"No Kubernetes cluster configured. Set {EnvironmentVariable} to a kubectl context " +
                "(e.g. kind-kind) to run the adapter-pool end-to-end tests.");
        }

        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context);
        var client = new Kubernetes(config);
        var gateway = new CommunicationPoolKubernetesGateway(client);

        // PlatformNamespace unset: the pool lands in PoolNamespace, which is where its tenant's CR
        // lives and therefore the only namespace in which the owner reference is valid.
        var options = new OperatorOptions { PoolNamespace = Namespace };
        var reconciler = new WorkloadReconciler(
            Substitute.For<IHelmRunner>(),
            gateway,
            Substitute.For<IWorkloadDiagnosticsCollector>(),
            new ServiceCollection().AddSingleton(Substitute.For<IOperatorHubInvoker>()).BuildServiceProvider(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WorkloadReconciler>.Instance);

        await EnsureNamespaceAsync(client);
        await DeleteDeploymentIfPresentAsync(client);
        await DeleteCommunicationPoolIfPresentAsync(client);

        return (client, reconciler, gateway, options);
    }

    private static async Task EnsureNamespaceAsync(IKubernetes client)
    {
        try
        {
            await client.CoreV1.ReadNamespaceAsync(Namespace);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            try
            {
                await client.CoreV1.CreateNamespaceAsync(new V1Namespace
                {
                    Metadata = new V1ObjectMeta { Name = Namespace },
                });
            }
            catch (HttpOperationException conflict)
                when (conflict.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Another test in this class won the race; that is the outcome we wanted.
            }
        }
    }

    private static async Task DeleteDeploymentIfPresentAsync(IKubernetes client)
    {
        try
        {
            await client.AppsV1.DeleteNamespacedDeploymentAsync(Release, Namespace);
            await WaitUntilAsync(async () => !await DeploymentExistsAsync(client),
                "the leftover deployment to disappear");
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Nothing to clean up.
        }
    }

    private static async Task DeleteCommunicationPoolIfPresentAsync(IKubernetes client)
    {
        try
        {
            await client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                "octo-mesh.meshmakers.io", "v1alpha1", Namespace, "communicationpools", CrName);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Nothing to clean up.
        }
    }

    private static Task CreateCommunicationPoolAsync(CommunicationPoolKubernetesGateway gateway) =>
        gateway.CreateCommunicationPoolAsync(Namespace, new E2ECommunicationPoolResource
        {
            Metadata = new E2EMetadata { Name = CrName, Namespace = Namespace },
            Spec = new E2ESpec { TenantId = TenantId, PoolRtId = PoolRtId },
        });

    private static Task CreatePoolMemberDeploymentAsync(IKubernetes client, int replicas)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/instance"] = Release,
            ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator-e2e",
        };

        return client.AppsV1.CreateNamespacedDeploymentAsync(new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = Release, NamespaceProperty = Namespace, Labels = labels },
            Spec = new V1DeploymentSpec
            {
                Replicas = replicas,
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        TerminationGracePeriodSeconds = 0,
                        Containers = [new V1Container { Name = "member", Image = PauseImage }],
                    },
                },
            },
        }, Namespace);
    }

    private static async Task<bool> DeploymentExistsAsync(IKubernetes client)
    {
        try
        {
            await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<int?> ReadSpecReplicasAsync(IKubernetes client)
    {
        var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace);
        return deployment.Spec?.Replicas;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what,
        int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new TimeoutException($"Timed out after {timeoutSeconds}s waiting for {what}.");
    }

    [Test]
    [NotInParallel(nameof(AdapterPoolKindE2ETests))]
    public async Task AdapterPool_ScalesOneToThreeAndBackToOne()
    {
        var (client, reconciler, _, _) = await ArrangeAsync();

        try
        {
            await CreatePoolMemberDeploymentAsync(client, replicas: 1);
            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(1);

            var scaledUp = await reconciler.ScaleAsync(ScaleDto(3), CancellationToken.None);
            await Assert.That(scaledUp).IsEqualTo(1); // one Deployment patched

            await WaitUntilAsync(async () => await ReadSpecReplicasAsync(client) == 3,
                "the pool to report three members");
            // status.replicas is written by the ReplicaSet controller once the pods exist, so this
            // is the cluster agreeing rather than the spec merely having been accepted.
            await WaitUntilAsync(async () =>
                    (await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace)).Status?.Replicas == 3,
                "three member pods to be created");

            var scaledDown = await reconciler.ScaleAsync(ScaleDto(1), CancellationToken.None);
            await Assert.That(scaledDown).IsEqualTo(1);

            await WaitUntilAsync(async () => await ReadSpecReplicasAsync(client) == 1,
                "the pool to come back to one member");
        }
        finally
        {
            await DeleteDeploymentIfPresentAsync(client);
        }
    }

    [Test]
    [NotInParallel(nameof(AdapterPoolKindE2ETests))]
    public async Task DeletingTheLendingTenantsCommunicationPool_GarbageCollectsThePool()
    {
        var (client, _, gateway, _) = await ArrangeAsync();

        try
        {
            await CreateCommunicationPoolAsync(gateway);
            await CreatePoolMemberDeploymentAsync(client, replicas: 1);

            var owner = await gateway.TryGetCommunicationPoolOwnerReferenceAsync(Namespace, CrName);
            await Assert.That(owner).IsNotNull();

            var patched = await gateway.SetDeploymentOwnerReferenceByInstanceAsync(Namespace, Release, owner!);
            await Assert.That(patched).IsEqualTo(1);

            var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace);
            await Assert.That(deployment.Metadata.OwnerReferences).IsNotNull();
            await Assert.That(deployment.Metadata.OwnerReferences.Single().Uid).IsEqualTo(owner!.Uid);

            // The tenant goes away: the operator deletes its CommunicationPool CR, and nothing else
            // is done about the pool. Kubernetes' garbage collector is what removes it.
            await DeleteCommunicationPoolIfPresentAsync(client);

            await WaitUntilAsync(async () => !await DeploymentExistsAsync(client),
                "the pool's deployment to be garbage-collected with its tenant");
        }
        finally
        {
            await DeleteDeploymentIfPresentAsync(client);
            await DeleteCommunicationPoolIfPresentAsync(client);
        }
    }

    private static ScaleWorkloadDto ScaleDto(int replicas) => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.AdapterPool,
        Replicas = replicas,
    };

    // Minimal wire shape for the CR; the operator's own resource model is internal to
    // CommunicationPoolManager and deliberately not exposed for test fixtures.
    private sealed class E2ECommunicationPoolResource
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion => "octo-mesh.meshmakers.io/v1alpha1";

        [JsonPropertyName("kind")]
        public string Kind => "CommunicationPool";

        [JsonPropertyName("metadata")]
        public E2EMetadata Metadata { get; init; } = new();

        [JsonPropertyName("spec")]
        public E2ESpec Spec { get; init; } = new();
    }

    private sealed class E2EMetadata
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("namespace")]
        public string Namespace { get; init; } = string.Empty;
    }

    private sealed class E2ESpec
    {
        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("poolRtId")]
        public string PoolRtId { get; init; } = string.Empty;
    }
}
