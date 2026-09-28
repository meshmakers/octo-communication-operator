using System.Diagnostics;
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
///     AB#5350 — a workload that was scaled outside helm must still deploy, and must still be the
///     size it was scaled to afterwards.
///
///     <para>
///     🔴 <b>Why this suite exists next to <see cref="AdapterPoolKindE2ETests" /> rather than inside
///     it.</b> That suite substitutes helm on purpose, and says so: nothing it proves needs helm, and
///     a directly-created Deployment carrying the release's <c>app.kubernetes.io/instance</c> label
///     is exactly the shape the scale path selects on. This defect is the opposite case. It lives
///     <i>entirely</i> in the helm layer — Helm 4 applies server-side, the chart renders
///     <c>spec.replicas</c>, the AB#4917 scale verb owns that field, and the apply is refused. With a
///     substituted helm there is no apply, no field manager and therefore no defect to reproduce:
///     the unit tests in <c>ReplicaCountPinTests</c> can only show that the operator passes
///     <c>--set replicaCount=&lt;live&gt;</c>, never that doing so is what makes the deploy succeed.
///     So this class runs the <b>real</b> <see cref="HelmRunner" /> over the <b>real</b> helm binary
///     against the <b>real</b> apiserver, and keeps the other suite's contract intact.
///     </para>
///
///     <para>
///     The chart is written to a temp directory by the test itself, and a thin wrapper around the
///     real runner skips the repository registration and points the chart reference at that
///     directory. Those are the only two things stood in for, and neither has anything to do with
///     field ownership: a repository is how a chart is fetched, not how it is applied.
///     </para>
///
///     <para>
///     <b>To run:</b>
///     <code>
///     OCTO_OPERATOR_E2E_KUBECONTEXT=kind-kind \
///       dotnet run --project tests/CommunicationOperator.Tests/CommunicationOperator.Tests.csproj \
///       -c DebugL --no-build --treenode-filter "/*/*/ScaledWorkloadRedeployKindE2ETests/*"
///     </code>
///     Needs <c>helm</c> ≥ 4 on PATH as well as the cluster: Helm 3 patches client-side, has no
///     notion of field ownership, and would report this defect as fixed on a build that still has
///     it. Both preconditions <b>skip</b> rather than pass when unmet.
///     </para>
/// </summary>
internal class ScaledWorkloadRedeployKindE2ETests
{
    private const string EnvironmentVariable = "OCTO_OPERATOR_E2E_KUBECONTEXT";
    private const string Namespace = "octo-scaled-redeploy-e2e";
    private const string TenantId = "e2escaled";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb1235e2e";
    private const string WorkloadRtId = "65d5c447b420da3fb1236e2e";
    private const string WorkloadName = "e2e-scaled-pool";
    private const string ChartName = "ab5350-member";

    // Present on every kind node, so the ReplicaSet controller can create pods without a registry
    // round trip — and they do have to become Ready here, because --rollback-on-failure implies
    // --wait. A pause container with no probes is Ready as soon as it is scheduled.
    private const string PauseImage = "registry.k8s.io/pause:3.10";

    private static string Release => WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);

    private sealed record Fixture(
        IKubernetes Client,
        WorkloadReconciler Reconciler,
        DeploymentSiteKubernetesGateway Gateway,
        IHelmRunner Helm,
        string ChartDirectory);

    private static async Task<Fixture> ArrangeAsync()
    {
        var context = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(context))
        {
            Skip.Test(
                $"No Kubernetes cluster configured. Set {EnvironmentVariable} to a kubectl context " +
                "(e.g. kind-kind) to run the scaled-redeploy end-to-end tests.");
        }

        await RequireHelm4Async();

        // The reconciler's helm arguments carry no --kube-context, so the context the rest of this
        // suite talks to is handed to the child process through helm's own environment variable.
        // Set on this process: nothing else in the test assembly shells out to helm.
        Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", context);

        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context);
        var client = new Kubernetes(config);
        var gateway = new DeploymentSiteKubernetesGateway(client);
        var options = new OperatorOptions { DeploymentSiteNamespace = Namespace };
        var chartDirectory = WriteChart();

        var helm = new LocalChartHelmRunner(
            new HelmRunner(new HelmProcessInvoker(NullLogger<HelmProcessInvoker>.Instance),
                NullLogger<HelmRunner>.Instance, Microsoft.Extensions.Options.Options.Create(options)),
            ChartName, chartDirectory);

        var reconciler = new WorkloadReconciler(
            helm,
            gateway,
            Substitute.For<IWorkloadDiagnosticsCollector>(),
            new ServiceCollection().AddSingleton(Substitute.For<IOperatorHubInvoker>()).BuildServiceProvider(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WorkloadReconciler>.Instance);

        await EnsureNamespaceAsync(client);
        // A leftover release from an earlier run would be at whatever size that run left behind,
        // which is the one thing these tests read.
        await helm.UninstallAsync(Release, Namespace, CancellationToken.None);
        await WaitUntilAsync(async () => !await DeploymentExistsAsync(client),
            "the leftover deployment to disappear");

        return new Fixture(client, reconciler, gateway, helm, chartDirectory);
    }

    /// <summary>
    ///     Skips unless the helm on PATH is Helm 4 or newer. 🔴 A version check rather than a
    ///     presence check: Helm 3's client-side three-way merge has no notion of field ownership, so
    ///     it would deploy a scaled workload happily and this suite would report the defect fixed on
    ///     a build where nothing is.
    /// </summary>
    private static async Task RequireHelm4Async()
    {
        string version;
        try
        {
            var probe = await new HelmProcessInvoker(NullLogger<HelmProcessInvoker>.Instance)
                .InvokeAsync(["version", "--short"], CancellationToken.None);
            version = probe.StdOut.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Skip.Test("No helm binary on PATH; the scaled-redeploy end-to-end tests need helm 4 or newer.");
            return;
        }

        var major = version.TrimStart('v').Split('.').FirstOrDefault();
        if (!int.TryParse(major, out var majorVersion) || majorVersion < 4)
        {
            Skip.Test(
                $"helm '{version}' is older than 4. Helm 3 applies client-side and cannot reproduce a " +
                "server-side-apply field-ownership conflict, so this suite would pass without proving anything.");
        }
    }

    /// <summary>
    ///     The smallest chart that has the defect: one Deployment whose <c>spec.replicas</c> comes
    ///     from <c>replicaCount</c>, labelled with the release instance the scale verb selects on.
    ///     That is the whole of the collision — the operator patches the field the chart renders.
    /// </summary>
    private static string WriteChart()
    {
        var directory = Directory.CreateTempSubdirectory("octo-ab5350-chart-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, "templates"));

        File.WriteAllText(Path.Combine(directory, "Chart.yaml"),
            $"apiVersion: v2\nname: {ChartName}\nversion: 0.1.0\ntype: application\n");
        File.WriteAllText(Path.Combine(directory, "values.yaml"), "replicaCount: 1\n");
        // A plain (non-interpolated) raw string: every brace in here belongs to Helm's template
        // syntax, so the image is substituted afterwards rather than fighting C# over braces.
        const string deploymentTemplate =
            """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{ .Release.Name }}
              labels:
                app.kubernetes.io/instance: {{ .Release.Name }}
            spec:
              replicas: {{ .Values.replicaCount }}
              selector:
                matchLabels:
                  app.kubernetes.io/instance: {{ .Release.Name }}
              template:
                metadata:
                  labels:
                    app.kubernetes.io/instance: {{ .Release.Name }}
                spec:
                  terminationGracePeriodSeconds: 0
                  containers:
                    - name: member
                      image: __IMAGE__

            """;
        File.WriteAllText(Path.Combine(directory, "templates", "deployment.yaml"),
            deploymentTemplate.Replace("__IMAGE__", PauseImage, StringComparison.Ordinal));

        return directory;
    }

    /// <summary>
    ///     Delegates every operation to the real <see cref="HelmRunner" /> — same arguments, same
    ///     binary, same server-side apply — with two substitutions that have nothing to do with field
    ///     ownership: the chart repository is not registered (there is none), and the chart reference
    ///     is the local directory the test wrote.
    /// </summary>
    private sealed class LocalChartHelmRunner(IHelmRunner inner, string chartName, string chartDirectory) : IHelmRunner
    {
        public Task EnsureRepoAsync(string alias, string url, string? username, string? password,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpgradeInstallAsync(string release, string chart, string version, string @namespace,
            IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
            CancellationToken cancellationToken) =>
            inner.UpgradeInstallAsync(release, Resolve(chart), version, @namespace, valuesFiles, setValues,
                cancellationToken);

        public Task UpgradeInstallDryRunAsync(string release, string chart, string version, string @namespace,
            IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
            CancellationToken cancellationToken) =>
            inner.UpgradeInstallDryRunAsync(release, Resolve(chart), version, @namespace, valuesFiles, setValues,
                cancellationToken);

        public Task UninstallAsync(string release, string @namespace, CancellationToken cancellationToken) =>
            inner.UninstallAsync(release, @namespace, cancellationToken);

        public Task<HelmReleaseRevision?> GetLatestReleaseRevisionAsync(string release, string @namespace,
            CancellationToken cancellationToken) =>
            inner.GetLatestReleaseRevisionAsync(release, @namespace, cancellationToken);

        public Task<string?> GetInstalledChartVersionAsync(string release, string chart, string @namespace,
            CancellationToken cancellationToken) =>
            inner.GetInstalledChartVersionAsync(release, chart, @namespace, cancellationToken);

        // The reconciler builds "{repoAlias}/{chartName}"; anything else is a chart reference a test
        // passed deliberately and is left alone.
        private string Resolve(string chart) =>
            chart.EndsWith($"/{chartName}", StringComparison.Ordinal) ? chartDirectory : chart;
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

    private static async Task<int?> ReadSpecReplicasAsync(IKubernetes client) =>
        (await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace)).Spec?.Replicas;

    /// <summary>
    ///     Does the operator's own field manager own <c>spec.replicas</c> on the live object? This is
    ///     the premise of the whole defect, read off the apiserver rather than assumed: without it a
    ///     green run below would only prove that an unconflicted deploy works.
    /// </summary>
    private static async Task<bool> OperatorOwnsSpecReplicasAsync(IKubernetes client)
    {
        var deployment = await client.AppsV1.ReadNamespacedDeploymentAsync(Release, Namespace);
        var entry = deployment.Metadata?.ManagedFields?.FirstOrDefault(f =>
            f.Manager == DeploymentSiteKubernetesGateway.FieldManagerName);
        return entry != null && KubernetesJson.Serialize(entry).Contains("f:replicas", StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 60)
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

    /// <summary>
    ///     The defect and the fix in one run: a deploy that would have failed before this work, and
    ///     the failure itself demonstrated in the middle of it so the test cannot pass by the
    ///     conflict simply never happening.
    /// </summary>
    [Test]
    [NotInParallel(nameof(ScaledWorkloadRedeployKindE2ETests))]
    public async Task ScaledWorkload_Redeploys_AndStaysAtItsScaledSize()
    {
        var fixture = await ArrangeAsync();
        var (client, reconciler, _, helm, chartDirectory) = fixture;

        try
        {
            // 1. First install — nothing is running, so nothing is pinned and the chart decides.
            await reconciler.DeployAsync(DeployDto(), CancellationToken.None);
            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(1);

            // 2. The on-demand lifecycle scales the release outside helm (AB#4917). The patch is
            //    what takes ownership of spec.replicas away from helm's applier.
            var patched = await reconciler.ScaleAsync(ScaleDto(3), CancellationToken.None);
            await Assert.That(patched).IsEqualTo(1);
            await WaitUntilAsync(async () => await ReadSpecReplicasAsync(client) == 3,
                "the release to report three replicas");
            await Assert.That(await OperatorOwnsSpecReplicasAsync(client)).IsTrue();

            // 3. 🔴 The defect, reproduced against the real apiserver: the deploy as it was before
            //    AB#5350 — the same helm call with no replicaCount pin — is refused by server-side
            //    apply, and helm's rollback is refused for the same reason.
            var conflict = await Assert.That(async () => await helm.UpgradeInstallAsync(
                    Release, $"unused/{ChartName}", string.Empty, Namespace, [],
                    new Dictionary<string, string>(), CancellationToken.None))
                .Throws<HelmException>();
            await Assert.That(HelmFieldOwnershipConflict.IsFieldOwnershipConflict(conflict!.StdErr)).IsTrue();
            await Assert.That(conflict!.StdErr).Contains(DeploymentSiteKubernetesGateway.FieldManagerName);
            await Assert.That(conflict!.StdErr).Contains(".spec.replicas");
            // The scale survived the refusal — nothing was applied, which is why a retry cannot help.
            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(3);

            // 4. The fix: the same deploy through the reconciler, which now reads the live count and
            //    pins it. Server-side apply accepts a value it is not being asked to change.
            await reconciler.DeployAsync(DeployDto(), CancellationToken.None);

            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(3);
            var revision = await helm.GetLatestReleaseRevisionAsync(Release, Namespace, CancellationToken.None);
            await Assert.That(revision).IsNotNull();
            // 🔴 Not merely "did not throw": the release has to be deployed. A helm upgrade that
            // fails and rolls back reports failure through the exception, but a release left in
            // 'failed' or 'pending-*' is the state this defect used to leave behind and the state
            // the next deploy then trips over.
            await Assert.That(revision!.Status).IsEqualTo("deployed");
        }
        finally
        {
            await CleanUpAsync(fixture);
        }
    }

    /// <summary>
    ///     The hibernation branch against a cluster rather than a substitute: a workload the
    ///     lifecycle has scaled to 0 redeploys, and is still down afterwards.
    ///
    ///     <para>
    ///     🔴 This is where the branch <i>order</i> is load-bearing, and the test fails if it is
    ///     reversed. The live count here is 0, and the live rule deliberately declines to pin 0 (see
    ///     <c>ResolveReplicaCountPinAsync</c>) — so without the hibernation branch in front of it,
    ///     helm would apply the chart's own count against a <c>spec.replicas</c> the operator owns
    ///     at 0, and this deploy would fail the AB#5350 conflict instead of keeping the workload
    ///     down.
    ///     </para>
    ///
    ///     <para>
    ///     The scale to 0 comes first because that is how a workload actually reaches hibernation:
    ///     <c>Draining → scale 0 → ack → Hibernated</c>. A deploy that arrives <i>during</i>
    ///     draining, with the flag already set and replicas still above 0, is the one case agreement
    ///     cannot fix — there the deploy genuinely has to change a field another manager owns, and it
    ///     conflicts (measured 2026-09-24). It is also transient and self-correcting: the drain
    ///     finishes in seconds and the next deploy lands on the state this test describes.
    ///     </para>
    /// </summary>
    [Test]
    [NotInParallel(nameof(ScaledWorkloadRedeployKindE2ETests))]
    public async Task HibernatedWorkload_Redeploys_AndStaysDown()
    {
        var fixture = await ArrangeAsync();
        var (client, reconciler, _, helm, _) = fixture;

        try
        {
            await reconciler.DeployAsync(DeployDto(), CancellationToken.None);
            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(1);

            // How hibernation happens: the lifecycle scales the release to 0 through the AB#4917
            // verb, which leaves the operator owning spec.replicas at 0.
            await reconciler.ScaleAsync(ScaleDto(0), CancellationToken.None);
            await WaitUntilAsync(async () => await ReadSpecReplicasAsync(client) == 0,
                "the release to be scaled to zero");
            await Assert.That(await OperatorOwnsSpecReplicasAsync(client)).IsTrue();

            await reconciler.DeployAsync(DeployDto(hibernated: true), CancellationToken.None);

            await Assert.That(await ReadSpecReplicasAsync(client)).IsEqualTo(0);
            var revision = await helm.GetLatestReleaseRevisionAsync(Release, Namespace, CancellationToken.None);
            await Assert.That(revision!.Status).IsEqualTo("deployed");
        }
        finally
        {
            await CleanUpAsync(fixture);
        }
    }

    private static async Task CleanUpAsync(Fixture fixture)
    {
        try
        {
            await fixture.Helm.UninstallAsync(Release, Namespace, CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(fixture.ChartDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not worth failing a passing test over.
            }
        }
    }

    private static WorkloadDeployedDto DeployDto(bool hibernated = false) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.AdapterPool,
        RepositoryUrl = "https://example.invalid/charts",
        ChartName = ChartName,
        // Empty on purpose: helm rejects --version for a chart given as a local directory, and an
        // unpinned non-reconciliation deploy is exactly the path that omits the flag.
        ChartVersion = string.Empty,
        Values = [],
        Hibernated = hibernated,
    };

    private static ScaleWorkloadDto ScaleDto(int replicas) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.AdapterPool,
        Replicas = replicas,
    };
}
