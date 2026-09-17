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
///     The things these tests prove cannot be proved with a substitute. Kubernetes' garbage
///     collector is a real controller with real rules — in particular the rule that a dependent
///     whose owner sits in another namespace is <i>deleted</i> rather than merely unowned, which is
///     why the operator refuses to write such a reference at all. And a merge patch against the
///     Deployments of a release either moves <c>spec.replicas</c> on the live object or it does not.
///     </para>
///
///     <para>
///     Four tests, in two pairs. <b>Scale</b> (1 → 3 → 1) and <b>garbage collection</b> on tenant
///     delete cover the happy path. <b>Cross-namespace destruction</b> and the operator's
///     <b>refusal</b> cover §7.1a from both sides: the first shows a cross-namespace owner
///     reference destroying a pool whose owner is still alive — the fact the refusal exists for,
///     and the one thing a substituted gateway can never demonstrate — and the second shows the
///     operator declining to write one against that same live apiserver.
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
///       -c DebugL --treenode-filter "/*/*/AdapterPoolKindE2ETests/*"
///     </code>
///     🔴 <c>--treenode-filter</c>, not <c>--filter</c>. Under the Microsoft.Testing.Platform runner
///     this repo opts into, <c>--filter</c> is accepted, matches nothing, and exits <b>5</b> with
///     "no tests were run" — which reads like an environment problem rather than a typo.
///     <para>
///     Without the environment variable every test in this class reports as <b>skipped</b>, never as
///     passed — a green run on a machine with no cluster would be a lie about what was verified.
///     </para>
///     Requires the <c>communicationpools.octo-mesh.meshmakers.io</c> CRD and permission to create
///     the <c>octo-pool-e2e</c> and <c>octo-pool-e2e-platform</c> namespaces.
///     </para>
/// </summary>
internal class AdapterPoolKindE2ETests
{
    private const string EnvironmentVariable = "OCTO_OPERATOR_E2E_KUBECONTEXT";
    private const string Namespace = "octo-pool-e2e";

    // A second namespace standing in for a distinct OperatorOptions.PlatformNamespace. The CR — the
    // only object that represents a tenant — never moves out of PoolNamespace, so a pool routed
    // here is a pool whose owner lives somewhere else. That is the topology §7.1a refuses to write
    // an owner reference in, and the two tests below prove both halves of the refusal.
    private const string PlatformNamespace = "octo-pool-e2e-platform";
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
        CommunicationPoolKubernetesGateway Gateway, OperatorOptions Options)> ArrangeAsync(
        string? platformNamespace = null)
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
        // lives and therefore the only namespace in which the owner reference is valid. The tests
        // that pass one in are exercising the opposite topology deliberately.
        var options = new OperatorOptions { PoolNamespace = Namespace, PlatformNamespace = platformNamespace };
        var reconciler = new WorkloadReconciler(
            Substitute.For<IHelmRunner>(),
            gateway,
            Substitute.For<IWorkloadDiagnosticsCollector>(),
            new ServiceCollection().AddSingleton(Substitute.For<IOperatorHubInvoker>()).BuildServiceProvider(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WorkloadReconciler>.Instance);

        await EnsureNamespaceAsync(client, Namespace);
        await DeleteDeploymentIfPresentAsync(client, Namespace);
        await DeleteCommunicationPoolIfPresentAsync(client);

        if (platformNamespace != null)
        {
            await EnsureNamespaceAsync(client, platformNamespace);
            await DeleteDeploymentIfPresentAsync(client, platformNamespace);
            // 🔴 Events outlive the objects that produced them (default TTL 1h). A leftover
            // OwnerRefInvalidNamespace from an earlier run would let the cross-namespace test
            // assert its own evidence without having produced any this time.
            await DeleteReleaseEventsAsync(client, platformNamespace);
        }

        return (client, reconciler, gateway, options);
    }

    private static async Task EnsureNamespaceAsync(IKubernetes client, string @namespace)
    {
        try
        {
            await client.CoreV1.ReadNamespaceAsync(@namespace);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            try
            {
                await client.CoreV1.CreateNamespaceAsync(new V1Namespace
                {
                    Metadata = new V1ObjectMeta { Name = @namespace },
                });
            }
            catch (HttpOperationException conflict)
                when (conflict.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Another test in this class won the race; that is the outcome we wanted.
            }
        }
    }

    private static async Task DeleteDeploymentIfPresentAsync(IKubernetes client, string @namespace)
    {
        try
        {
            await client.AppsV1.DeleteNamespacedDeploymentAsync(Release, @namespace);
            await WaitUntilAsync(async () => !await DeploymentExistsAsync(client, @namespace),
                $"the leftover deployment in '{@namespace}' to disappear");
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Nothing to clean up.
        }
    }

    private static async Task DeleteReleaseEventsAsync(IKubernetes client, string @namespace)
    {
        var events = await client.CoreV1.ListNamespacedEventAsync(@namespace,
            fieldSelector: $"involvedObject.name={Release}");
        foreach (var @event in events.Items)
        {
            try
            {
                await client.CoreV1.DeleteNamespacedEventAsync(@event.Metadata.Name, @namespace);
            }
            catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already aged out between the list and the delete.
            }
        }
    }

    /// <summary>
    ///     🔴 Deletes the CR and waits for it to actually be gone, rather than for the apiserver to
    ///     have accepted the delete. A deployed operator registers a finalizer on this kind, so the
    ///     object lingers in <c>Terminating</c> until that operator clears it — and a create issued
    ///     in the meantime fails with <c>409 AlreadyExists: object is being deleted</c>, which
    ///     surfaces as a failure in whichever test happens to run next rather than in this one.
    ///     On a cluster with no operator deployed there is no finalizer and the wait returns
    ///     immediately.
    /// </summary>
    private static async Task DeleteCommunicationPoolIfPresentAsync(IKubernetes client)
    {
        try
        {
            await client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                "octo-mesh.meshmakers.io", "v1alpha1", Namespace, "communicationpools", CrName);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        await WaitUntilAsync(async () => !await CommunicationPoolStillPresentAsync(client),
            $"the CommunicationPool CR '{CrName}' to finish terminating");
    }

    private static async Task<bool> CommunicationPoolStillPresentAsync(IKubernetes client)
    {
        try
        {
            await client.CustomObjects.GetNamespacedCustomObjectAsync(
                "octo-mesh.meshmakers.io", "v1alpha1", Namespace, "communicationpools", CrName);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static Task CreateCommunicationPoolAsync(CommunicationPoolKubernetesGateway gateway) =>
        gateway.CreateCommunicationPoolAsync(Namespace, new E2ECommunicationPoolResource
        {
            Metadata = new E2EMetadata { Name = CrName, Namespace = Namespace },
            Spec = new E2ESpec { TenantId = TenantId, PoolRtId = PoolRtId },
        });

    private static Task CreatePoolMemberDeploymentAsync(IKubernetes client, int replicas,
        string @namespace = Namespace)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/instance"] = Release,
            ["octo-mesh.meshmakers.io/managed-by"] = "communication-operator-e2e",
        };

        return client.AppsV1.CreateNamespacedDeploymentAsync(new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = Release, NamespaceProperty = @namespace, Labels = labels },
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
        }, @namespace);
    }

    private static async Task<bool> DeploymentExistsAsync(IKubernetes client, string @namespace = Namespace)
    {
        try
        {
            await client.AppsV1.ReadNamespacedDeploymentAsync(Release, @namespace);
            return true;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> CommunicationPoolExistsAsync(CommunicationPoolKubernetesGateway gateway) =>
        await gateway.CommunicationPoolExistsAsync(Namespace, CrName);

    /// <summary>
    ///     Did the garbage collector delete the release's Deployment <i>because</i> its owner sat in
    ///     another namespace? <c>OwnerRefInvalidNamespace</c> is the reason string the GC emits for
    ///     exactly that case, and it is the only evidence that ties the deletion to the rule under
    ///     test rather than to an unrelated cause.
    /// </summary>
    private static async Task<bool> GarbageCollectorRejectedTheOwnerRefAsync(IKubernetes client)
    {
        var events = await client.CoreV1.ListNamespacedEventAsync(PlatformNamespace,
            fieldSelector: $"involvedObject.name={Release},reason=OwnerRefInvalidNamespace");
        return events.Items.Count > 0;
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
            await DeleteDeploymentIfPresentAsync(client, Namespace);
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
            await DeleteDeploymentIfPresentAsync(client, Namespace);
            await DeleteCommunicationPoolIfPresentAsync(client);
        }
    }

    /// <summary>
    ///     AB#4924 §7.1a — the premise the refusal rests on, asserted rather than assumed.
    ///
    ///     <para>
    ///     <see cref="PoolOwnerReferenceTests" /> proves the operator declines to write a
    ///     cross-namespace owner reference. It cannot prove <i>why that matters</i>: with a
    ///     substituted gateway there is no garbage collector, so "Kubernetes deletes a dependent
    ///     whose owner lives in another namespace" stays an unverified claim in a comment, and the
    ///     refusal looks like caution rather than the difference between a pool that survives and
    ///     one that is destroyed seconds after deploy.
    ///     </para>
    ///
    ///     <para>
    ///     🔴 The owner is never deleted here. The CR stays alive and healthy for the whole test,
    ///     and the Deployment in the foreign namespace dies anyway — that, and not the
    ///     owner-deleted path in the test above, is what makes writing such a reference a
    ///     destructive act.
    ///     </para>
    /// </summary>
    [Test]
    [NotInParallel(nameof(AdapterPoolKindE2ETests))]
    public async Task CrossNamespaceOwner_DestroysThePoolWhileItsOwnerIsStillAlive()
    {
        var (client, _, gateway, _) = await ArrangeAsync(PlatformNamespace);

        try
        {
            await CreateCommunicationPoolAsync(gateway);
            // The pool lands in the platform namespace; its tenant's CR stays in the pool
            // namespace. This is exactly the split a distinct PlatformNamespace produces.
            await CreatePoolMemberDeploymentAsync(client, replicas: 1, PlatformNamespace);

            var owner = await gateway.TryGetCommunicationPoolOwnerReferenceAsync(Namespace, CrName);
            await Assert.That(owner).IsNotNull();

            var patched = await gateway.SetDeploymentOwnerReferenceByInstanceAsync(
                PlatformNamespace, Release, owner!);
            await Assert.That(patched).IsEqualTo(1);

            await WaitUntilAsync(async () => !await DeploymentExistsAsync(client, PlatformNamespace),
                "the cross-namespace-owned deployment to be destroyed by the garbage collector",
                timeoutSeconds: 120);

            // 🔴 "It disappeared" is not the assertion — a Deployment can vanish for reasons that
            // have nothing to do with owner references, and this test would then pass while
            // proving nothing. The garbage collector names its own cause, so demand it by name.
            await Assert.That(await GarbageCollectorRejectedTheOwnerRefAsync(client)).IsTrue();

            // The owner never went anywhere. Had this test deleted the CR, the deletion above
            // would have proved nothing that the previous test does not already prove.
            await Assert.That(await CommunicationPoolExistsAsync(gateway)).IsTrue();
        }
        finally
        {
            await DeleteDeploymentIfPresentAsync(client, PlatformNamespace);
            await DeleteCommunicationPoolIfPresentAsync(client);
        }
    }

    /// <summary>
    ///     AB#4924 §7.1a — the other half: the operator, run against a real apiserver, does not do
    ///     the thing the test above shows to be fatal. A pool routed to a distinct platform
    ///     namespace deploys with <b>no</b> owner reference and is still there afterwards.
    ///
    ///     <para>
    ///     Helm is substituted as everywhere else in this class, so the Deployment is created up
    ///     front — it stands in for what <c>helm upgrade --install</c> would have produced, and
    ///     carries the release's <c>app.kubernetes.io/instance</c> label, which is the only thing
    ///     the owner-stamping path selects on. If the refusal ever regressed, the stamp would find
    ///     this Deployment and the garbage collector would take it.
    ///     </para>
    ///
    ///     <para>
    ///     🔴 <b>What this actually pins, established by mutation rather than assumed.</b> Deleting
    ///     the namespace guard in <c>TryResolvePoolOwnerReferenceAsync</c> on its own does
    ///     <i>not</i> fail this test, and that is not a weakness in the test — it is a fact about
    ///     the code worth knowing. The CR lookup and the owner-reference write both take the same
    ///     <c>ns</c>, so with the guard gone the lookup simply moves to the platform namespace,
    ///     finds no CR there and returns null. The guard is defence in depth; the load-bearing
    ///     protection is that those two namespaces are one variable.
    ///     </para>
    ///
    ///     <para>
    ///     The regression this test does catch is the realistic one: repointing the lookup at
    ///     <c>_options.PoolNamespace</c> — which the guard's own warning text invites, since it
    ///     says that is where the CR lives — while the write stays on <c>ns</c>. That combination
    ///     produces a genuine cross-namespace reference, and this test fails on the empty-owner
    ///     assertion before the garbage collector has even acted.
    ///     </para>
    /// </summary>
    [Test]
    [NotInParallel(nameof(AdapterPoolKindE2ETests))]
    public async Task DeployingAPoolIntoAPlatformNamespace_WritesNoOwnerAndThePoolSurvives()
    {
        var (client, reconciler, gateway, options) = await ArrangeAsync(PlatformNamespace);

        try
        {
            // The CR exists and is resolvable, so a regression would find an owner to write
            // rather than merely having none available.
            await CreateCommunicationPoolAsync(gateway);
            await CreatePoolMemberDeploymentAsync(client, replicas: 1, PlatformNamespace);

            await Assert.That(reconciler.ResolveNamespace(WorkloadTypeDto.AdapterPool))
                .IsEqualTo(PlatformNamespace);
            await Assert.That(options.PoolNamespace).IsEqualTo(Namespace);

            await reconciler.DeployAsync(DeployDto(), CancellationToken.None);

            var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(Release, PlatformNamespace);
            await Assert.That(deployment.Metadata.OwnerReferences ?? []).IsEmpty();

            // Give the garbage collector the same window the test above needed to act in. An
            // immediate assertion would pass even if a reference had been written.
            await Task.Delay(TimeSpan.FromSeconds(20));
            await Assert.That(await DeploymentExistsAsync(client, PlatformNamespace)).IsTrue();
        }
        finally
        {
            await DeleteDeploymentIfPresentAsync(client, PlatformNamespace);
            await DeleteCommunicationPoolIfPresentAsync(client);
        }
    }

    private static WorkloadDeployedDto DeployDto() => new()
    {
        TenantId = TenantId,
        PoolRtId = PoolRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.AdapterPool,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "octo-mesh-adapter",
        ChartVersion = "1.2.3",
        Values = [],
    };

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
