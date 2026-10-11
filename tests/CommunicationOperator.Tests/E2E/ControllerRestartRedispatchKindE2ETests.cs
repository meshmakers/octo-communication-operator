using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
///     AB#6471 — a controller restart while deploys are in flight must not deploy the same workloads
///     twice nor leave <c>Error</c> behind for releases helm has deployed.
///
///     <para>
///     Real <see cref="WorkloadReconciler" />, real <see cref="HelmRunner" />, real helm 4 binary, real
///     apiserver (kind). Only the controller is simulated, by the three behaviours that matter: the
///     operator's SignalR handlers run one after the other <i>per connection</i>; a restart kills the
///     connection (queued dispatches are lost, a handler already running keeps running, and its
///     report goes into the void); on re-registration the controller re-dispatches every workload
///     still <c>Pending</c> with <c>IsReconciliation=true</c> (AB#4894). The handler mirrors
///     <c>OperatorHubService.WorkloadDeployedAsync</c>: success → <c>Deployed</c>, exception → <c>Error</c>.
///     </para>
///
///     <para>
///     To run: <c>OCTO_OPERATOR_E2E_KUBECONTEXT=kind-kind dotnet run --project ... --treenode-filter
///     "/*/*/ControllerRestartRedispatchKindE2ETests/*"</c>. <c>OCTO_OPERATOR_E2E_LEGACY=1</c> only prints
///     the outcome instead of asserting it (counter-check against a build without the fix).
///     </para>
/// </summary>
internal class ControllerRestartRedispatchKindE2ETests
{
    private const string EnvironmentVariable = "OCTO_OPERATOR_E2E_KUBECONTEXT";
    private const string Namespace = "octo-redispatch-e2e";
    private const string TenantId = "e2eredisp";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb1235e3e";
    private const string ChartName = "ab6471-app";
    private const string PauseImage = "registry.k8s.io/pause:3.10";
    private const int WorkloadCount = 6;

    private static string RtId(int i) => $"65d5c447b420da3fb12360{i:00}";
    private static string Release(int i) => WorkloadReconciler.ReleaseName(TenantId, RtId(i));

    private enum State { Pending, Deployed, Error }

    [Test]
    [NotInParallel(nameof(ControllerRestartRedispatchKindE2ETests))]
    public async Task ControllerRestartWithDeploysInFlight_DeploysOncePerRequest_AndEndsDeployed()
    {
        var context = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(context))
        {
            Skip.Test($"No Kubernetes cluster configured. Set {EnvironmentVariable} (e.g. kind-kind).");
            return;
        }

        var legacy = Environment.GetEnvironmentVariable("OCTO_OPERATOR_E2E_LEGACY") == "1";
        Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", context);

        var client = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context));
        var options = new OperatorOptions { DeploymentSiteNamespace = Namespace };
        var chartDirectory = WriteChart();
        var helm = new LocalChartHelmRunner(
            new HelmRunner(new HelmProcessInvoker(NullLogger<HelmProcessInvoker>.Instance),
                NullLogger<HelmRunner>.Instance, Microsoft.Extensions.Options.Options.Create(options)),
            ChartName, chartDirectory);
        var reconciler = new WorkloadReconciler(helm, new DeploymentSiteKubernetesGateway(client),
            Substitute.For<IWorkloadDiagnosticsCollector>(),
            new ServiceCollection().AddSingleton(Substitute.For<IOperatorHubInvoker>()).BuildServiceProvider(),
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<WorkloadReconciler>.Instance);

        await EnsureNamespaceAsync(client);
        for (var i = 1; i <= WorkloadCount; i++)
        {
            await helm.UninstallAsync(Release(i), Namespace, CancellationToken.None);
        }

        try
        {
            // ---- simulated controller state: DeployWorkloadAsync sets Pending, the status report sets the result
            var store = new ConcurrentDictionary<int, State>();
            var message = new ConcurrentDictionary<int, string?>();
            var connectionAlive = true; // reports of a dead connection never reach the controller
            var started = new ConcurrentDictionary<int, DateTime>();

            async Task HandleAsync(int i, bool isReconciliation, Func<bool> reportReachesController)
            {
                // == OperatorHubService.WorkloadDeployedAsync
                bool success;
                string? status = null;
                started.TryAdd(i, DateTime.UtcNow);
                try
                {
                    await reconciler.DeployAsync(Dto(i, isReconciliation), CancellationToken.None);
                    success = true;
                }
                catch (Exception e)
                {
                    success = false;
                    status = e.Message;
                }

                if (reportReachesController())
                {
                    store[i] = success ? State.Deployed : State.Error;
                    message[i] = status;
                }
            }

            // ---- 1. dispatch N workloads on connection #1 (serial handler)
            for (var i = 1; i <= WorkloadCount; i++)
            {
                store[i] = State.Pending;
            }

            using var connection1 = new CancellationTokenSource();
            var inFlightWhenKilled = new TaskCompletionSource();
            var connection1Loop = Task.Run(async () =>
            {
                for (var i = 1; i <= WorkloadCount && !connection1.IsCancellationRequested; i++)
                {
                    if (i == 2)
                    {
                        inFlightWhenKilled.TrySetResult();
                    }

                    // The handler itself is not interruptible: a killed connection stops the loop, not the call.
                    await HandleAsync(i, false, () => Volatile.Read(ref connectionAlive));
                }
            });

            // ---- 2. restart the controller while workload 2 is deploying (1 is done and reported)
            await inFlightWhenKilled.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromSeconds(2));
            Volatile.Write(ref connectionAlive, false);
            connection1.Cancel();
            Console.WriteLine($"[e2e] controller restarted; states: {string.Join(", ", store.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}");

            // ---- 3. operator re-registers on connection #2; the controller re-dispatches every Pending workload
            //         (serial handler again) while connection #1's running handler is still busy
            var connection2 = new object();
            var pending = store.Where(k => k.Value == State.Pending).Select(k => k.Key).OrderBy(k => k).ToList();
            Console.WriteLine($"[e2e] re-dispatching Pending: {string.Join(",", pending)}");
            foreach (var i in pending)
            {
                await HandleAsync(i, true, () => true);
            }

            await connection1Loop; // let the old handler finish, its report is lost

            // ---- 4. another lost report on a deployed release: controller re-dispatches it, helm must stay untouched
            store[3] = State.Pending;
            await HandleAsync(3, true, () => true);

            // ---- 5. a user-triggered redeploy of workload 1 still deploys
            await HandleAsync(1, false, () => true);

            // ---- evidence
            Console.WriteLine("[e2e] ===== helm history per release =====");
            var revisions = new Dictionary<int, List<(int Rev, string Status, string? Description)>>();
            for (var i = 1; i <= WorkloadCount; i++)
            {
                revisions[i] = await ReadHistoryAsync(Release(i));
                Console.WriteLine($"[e2e] workload {i} ({Release(i)}) status={store[i]}{(message[i] is { } m ? " msg='" + m + "'" : "")}");
                foreach (var r in revisions[i])
                {
                    Console.WriteLine($"[e2e]     rev {r.Rev} {r.Status} \"{r.Description}\"");
                }
            }

            if (legacy)
            {
                return;
            }

            for (var i = 1; i <= WorkloadCount; i++)
            {
                await Assert.That(store[i]).IsEqualTo(State.Deployed);
                var expectedRevisions = i == 1 ? 2 : 1; // workload 1 got the user-triggered redeploy on top
                await Assert.That(revisions[i].Count).IsEqualTo(expectedRevisions);
                await Assert.That(revisions[i].All(r => r.Description?.StartsWith("octo-deploy:", StringComparison.Ordinal) == true)).IsTrue();
                await Assert.That(revisions[i].Last().Status).IsEqualTo("deployed");
            }
        }
        finally
        {
            for (var i = 1; i <= WorkloadCount; i++)
            {
                await helm.UninstallAsync(Release(i), Namespace, CancellationToken.None);
            }

            try
            {
                Directory.Delete(chartDirectory, true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static WorkloadDeployedDto Dto(int i, bool isReconciliation) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = RtId(i),
        WorkloadName = $"e2e-app-{i}",
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://example.invalid/charts",
        ChartName = ChartName,
        ChartVersion = string.Empty,
        ValuesYaml = string.Empty,
        Values = Array.Empty<ValueOverrideDto>(),
        IsReconciliation = isReconciliation,
    };

    private static string WriteChart()
    {
        var directory = Directory.CreateTempSubdirectory("octo-ab6471-chart-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, "templates"));
        File.WriteAllText(Path.Combine(directory, "Chart.yaml"),
            $"apiVersion: v2\nname: {ChartName}\nversion: 0.1.0\ntype: application\n");
        File.WriteAllText(Path.Combine(directory, "values.yaml"), "replicaCount: 1\n");
        // The readiness delay keeps each helm --wait running for several seconds: that is the window the
        // controller restart has to land in.
        const string deployment =
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
                    - name: app
                      image: __IMAGE__
                      readinessProbe:
                        exec:
                          command: ["/pause", "-v"]
                        initialDelaySeconds: 8
                        periodSeconds: 2

            """;
        File.WriteAllText(Path.Combine(directory, "templates", "deployment.yaml"),
            deployment.Replace("__IMAGE__", PauseImage, StringComparison.Ordinal));
        return directory;
    }

    private static async Task<List<(int, string, string?)>> ReadHistoryAsync(string release)
    {
        var result = await new HelmProcessInvoker(NullLogger<HelmProcessInvoker>.Instance)
            .InvokeAsync(["history", release, "--namespace", Namespace, "-o", "json"], CancellationToken.None);
        var list = new List<(int, string, string?)>();
        if (result.ExitCode != 0)
        {
            return list;
        }

        using var doc = JsonDocument.Parse(result.StdOut);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add((e.GetProperty("revision").GetInt32(), e.GetProperty("status").GetString() ?? "",
                e.TryGetProperty("description", out var d) ? d.GetString() : null));
        }

        return list.OrderBy(x => x.Item1).ToList();
    }

    private static async Task EnsureNamespaceAsync(IKubernetes client)
    {
        try
        {
            await client.CoreV1.ReadNamespaceAsync(Namespace);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = Namespace } });
        }
    }

    private sealed class LocalChartHelmRunner(IHelmRunner inner, string chartName, string chartDirectory) : IHelmRunner
    {
        public Task EnsureRepoAsync(string alias, string url, string? username, string? password,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpgradeInstallAsync(string release, string chart, string version, string @namespace,
            IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues, string? description,
            CancellationToken cancellationToken) =>
            inner.UpgradeInstallAsync(release, Resolve(chart), version, @namespace, valuesFiles, setValues,
                description, cancellationToken);

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

        private string Resolve(string chart) =>
            chart.EndsWith($"/{chartName}", StringComparison.Ordinal) ? chartDirectory : chart;
    }
}
