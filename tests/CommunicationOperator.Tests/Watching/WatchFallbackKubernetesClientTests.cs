using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Watching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Meshmakers.Octo.Communication.Operator.Tests.Watching;

// AB#6898 - the API server of prod-1 refuses watches with "a watch stream was requested by the client but the
// required storage feature RequestWatchProgress is disabled" (HTTP 500). KubeOps' ResourceWatcher retried the
// identical request forever, so a restarted operator pod never learned its DeploymentSites. These tests run the
// real k8s client against a fake API server that follows the matrix MEASURED on prod-1 (2026-10-18, raw GET
// watches): resourceVersion=0 alone works; allowWatchBookmarks=true with an empty or 0 resourceVersion fails;
// an exact resourceVersion works with and without bookmarks; sendInitialEvents=true fails. The first fix (PR #34)
// switched to resourceVersion=0 but kept the bookmarks flag and therefore did not recover prod-1.
public class WatchFallbackKubernetesClientTests
{
    private const string RefusalMessage =
        "a watch stream was requested by the client but the required storage feature RequestWatchProgress is disabled";

    [Test]
    public async Task Refused_unversioned_watch_falls_back_to_resourceVersion_zero_and_delivers_the_sites()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        var events = await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));

        await Assert.That(events.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "site-a", "site-b" });
        await Assert.That(events.All(e => e.Type == WatchEventType.Added)).IsTrue();
        // First the refused request without resourceVersion, then the classic one: resourceVersion=0 and - the
        // prod-1 rule - no bookmarks.
        await Assert.That(server.Requests.Select(r => r.ToString()).ToArray())
            .IsEquivalentTo(new[] { "rv=<none> bookmarks=true", "rv=0 bookmarks=false" });
        await Assert.That(state.IsClassic).IsTrue();
    }

    [Test]
    public async Task Fallback_is_remembered_so_later_watches_skip_the_refused_request_and_warn_only_once()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));
        await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));
        await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));

        // 1 refused + 3 classic. The retries of KubeOps' watcher no longer repeat the refused request.
        await Assert.That(server.Requests.Count(r => r.ResourceVersion is null)).IsEqualTo(1);
        await Assert.That(server.Requests.Count(r => r.ResourceVersion == "0" && r.Bookmarks != true)).IsEqualTo(3);
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Healthy_api_server_keeps_the_default_watch()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: false);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        var events = await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));

        await Assert.That(events.Count).IsEqualTo(2);
        // A healthy server keeps the default request, bookmarks included.
        await Assert.That(server.Requests.Select(r => r.ToString()).ToArray())
            .IsEquivalentTo(new[] { "rv=<none> bookmarks=true" });
        await Assert.That(state.IsClassic).IsFalse();
        await Assert.That(logger.Warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Forced_classic_mode_never_sends_the_unversioned_watch()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions { ForceClassicWatch = true }));
        using var client = CreateClient(server, state);

        var events = await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true));

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(server.Requests.Select(r => r.ToString()).ToArray())
            .IsEquivalentTo(new[] { "rv=0 bookmarks=false" });
        await Assert.That(state.IsClassic).IsTrue();
        // The forced mode is announced once at startup; no refusal happened, so no second warning.
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Reconnect_with_a_bookmark_resourceVersion_is_passed_through_untouched()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var state = new WatchModeState(new CapturingLogger<WatchModeState>(), MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        await Drain(client.WatchAsync<V1DeploymentSiteEntity>(resourceVersion: "5", allowWatchBookmarks: true));

        // A concrete resourceVersion keeps the caller's bookmark flag; prod-1 accepts that combination.
        await Assert.That(server.Requests.Select(r => r.ToString()).ToArray())
            .IsEquivalentTo(new[] { "rv=5 bookmarks=true" });
        await Assert.That(state.IsClassic).IsFalse();
    }

    [Test]
    public async Task Other_failures_are_not_swallowed()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: false) { FailWith = "etcd is on fire" };
        var state = new WatchModeState(new CapturingLogger<WatchModeState>(), MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        Exception? caught = null;
        try
        {
            await Drain(client.WatchAsync<V1DeploymentSiteEntity>());
        }
        catch (Exception e)
        {
            caught = e;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(state.IsClassic).IsFalse();
        await Assert.That(server.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Fake_server_matrix_matches_the_measurement_on_prod_1()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var plain = CreatePlainClient(server);
        var accepted = new List<string>();
        var refused = new List<string>();

        foreach (var (rv, bookmarks) in new (string?, bool?)[]
                 {
                     ("0", null), ("0", false), ("0", true), (null, true), ("5", true), ("5", false)
                 })
        {
            var request = new FakeApiServer.WatchRequest(rv, bookmarks, null);
            try
            {
                await Drain(plain.WatchAsync<V1DeploymentSiteEntity>(resourceVersion: rv, allowWatchBookmarks: bookmarks));

                accepted.Add(request.ToString());
            }
            catch (Exception e) when (WatchRefusal.IsRefusal(e))
            {
                refused.Add(request.ToString());
            }
        }

        await Assert.That(accepted.ToArray()).IsEquivalentTo(new[]
        {
            "rv=0 bookmarks=<unset>", "rv=0 bookmarks=false", "rv=5 bookmarks=true", "rv=5 bookmarks=false"
        });
        await Assert.That(refused.ToArray()).IsEquivalentTo(new[] { "rv=0 bookmarks=true", "rv=<none> bookmarks=true" });
    }

    [Test]
    public async Task Reconnect_without_bookmarks_restarts_classic_replays_the_state_and_loses_no_event()
    {
        // KubeOps remembers a resourceVersion for the reconnect only from BOOKMARK events (ResourceWatcher 13.2.0);
        // in classic mode there are none, so every reconnect is a classic start again: no endless refused
        // request, no event lost, a site that appeared while the stream was down arrives with the replay.
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        var first = await RunKubeOpsLikeSession(client, server, sessions: 1);
        server.Sites.Add(("site-c", 12)); // appears while the connection is down
        var rest = await RunKubeOpsLikeSession(client, server, sessions: 4);

        await Assert.That(first.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "site-a", "site-b" });
        await Assert.That(rest.Select(e => e.Name).Distinct().Order().ToArray()).IsEquivalentTo(new[] { "site-a", "site-b", "site-c" });
        // 1 refused request in total, every other watch is the classic start.
        await Assert.That(server.Requests.Count(r => r.ResourceVersion is null)).IsEqualTo(1);
        await Assert.That(server.Requests.Count).IsEqualTo(1 + 5);
        await Assert.That(server.Requests.Skip(1).All(r => r.ToString() == "rv=0 bookmarks=false")).IsTrue();
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Reconnect_with_the_last_event_resourceVersion_delivers_only_newer_events_and_loses_none()
    {
        // The wrapper must not interfere when a caller does hold a resourceVersion (event or bookmark): prod-1
        // accepts it with bookmarks, the stream continues exactly after it.
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var state = new WatchModeState(new CapturingLogger<WatchModeState>(), MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        var initial = await Drain(client.WatchAsync<V1DeploymentSiteEntity>(allowWatchBookmarks: true)); // fallback
        var lastVersion = server.Sites.Max(s => s.Version).ToString();
        server.Sites.Add(("site-c", 12));
        server.Sites.Add(("site-d", 13));
        var resumed = await Drain(client.WatchAsync<V1DeploymentSiteEntity>(resourceVersion: lastVersion, allowWatchBookmarks: true));

        await Assert.That(initial.Count).IsEqualTo(2);
        await Assert.That(resumed.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] { "site-c", "site-d" });
        await Assert.That(server.Requests.Last().ToString()).IsEqualTo($"rv={lastVersion} bookmarks=true");
    }

    [Test]
    public async Task Previous_fix_stays_refused_on_the_prod_1_matrix_resourceVersion_zero_with_bookmarks()
    {
        // Regression guard for the PR #34 mistake: resourceVersion=0 together with allowWatchBookmarks=true is
        // refused by prod-1, so a client that does not clear the flag never recovers.
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var plain = CreatePlainClient(server);

        Exception? caught = null;
        try
        {
            await Drain(plain.WatchAsync<V1DeploymentSiteEntity>(resourceVersion: "0", allowWatchBookmarks: true));
        }
        catch (Exception e)
        {
            caught = e;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(WatchRefusal.IsRefusal(caught)).IsTrue();
    }

    [Test]
    public async Task Refusal_is_recognised_in_status_message_response_body_and_inner_exception()
    {
        var status = new V1Status { Message = RefusalMessage, Code = 500 };

        await Assert.That(WatchRefusal.IsRefusal(new KubernetesException(status))).IsTrue();
        await Assert.That(WatchRefusal.IsRefusal(new InvalidOperationException("outer", new Exception(RefusalMessage)))).IsTrue();
        await Assert.That(WatchRefusal.IsRefusal(new KubernetesException(new V1Status { Message = "boom", Code = 500 }))).IsFalse();
        await Assert.That(WatchRefusal.IsRefusal(null)).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task ForceClassicWatch_binds_from_the_documented_environment_variable()
    {
        const string variable = "OPERATOR__FORCECLASSICWATCH";
        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var options = configuration.GetSection("Operator").Get<OperatorOptions>();

            await Assert.That(options!.ForceClassicWatch).IsTrue();
            await Assert.That(new OperatorOptions().ForceClassicWatch).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // The unmodified KubeOps client: what the operator used before AB#6898.
    private static KubeOps.KubernetesClient.KubernetesClient CreatePlainClient(FakeApiServer server)
    {
        var config = new KubernetesClientConfiguration { Host = "http://fake-api-server" };
        return new KubeOps.KubernetesClient.KubernetesClient(config, new Kubernetes(config, server));
    }

    private static WatchFallbackKubernetesClient CreateClient(FakeApiServer server, WatchModeState state)
    {
        var config = new KubernetesClientConfiguration { Host = "http://fake-api-server" };
        return new WatchFallbackKubernetesClient(config, new Kubernetes(config, server), state);
    }

    private static async Task<List<(WatchEventType Type, string Name)>> Drain(
        IAsyncEnumerable<(WatchEventType, V1DeploymentSiteEntity)> watch)
    {
        var result = new List<(WatchEventType, string)>();
        await foreach (var (type, entity) in watch)
        {
            result.Add((type, entity.Metadata.Name));
        }

        return result;
    }

    // Mirrors KubeOps 13.2.0 ResourceWatcher.ExecuteAsync: every session starts with the remembered version, which
    // is updated from BOOKMARK events only; a session ends when the stream ends. Collects the non-bookmark events.
    private static async Task<List<(WatchEventType Type, string Name)>> RunKubeOpsLikeSession(
        WatchFallbackKubernetesClient client, FakeApiServer server, int sessions)
    {
        var result = new List<(WatchEventType, string)>();
        string? currentVersion = null;
        for (var i = 0; i < sessions; i++)
        {
            server.NewSession();
            await foreach (var (type, entity) in client.WatchAsync<V1DeploymentSiteEntity>(
                               resourceVersion: currentVersion, allowWatchBookmarks: true))
            {
                if (type == WatchEventType.Bookmark)
                {
                    currentVersion = entity.Metadata.ResourceVersion;
                    continue;
                }

                result.Add((type, entity.Metadata.Name));
            }
        }

        return result;
    }

    // Behaves like the prod-1 API server, as MEASURED with raw watches (AB#6898, 2026-10-18):
    //   resourceVersion=0, no bookmarks flag ........ accepted (watch cache)
    //   resourceVersion=0, allowWatchBookmarks=true . HTTP 500 RequestWatchProgress
    //   no resourceVersion (any bookmark flag) ...... HTTP 500 (consistent read; bookmarks=true measured, false assumed)
    //   exact resourceVersion, bookmarks true/false . accepted, streams the events after it
    //   sendInitialEvents=true ...................... HTTP 500
    // refuseUnversionedWatch=false is a healthy cluster that accepts everything.
    private sealed class FakeApiServer(bool refuseUnversionedWatch) : DelegatingHandler
    {
        public sealed record WatchRequest(string? ResourceVersion, bool? Bookmarks, bool? SendInitialEvents)
        {
            public override string ToString() =>
                $"rv={ResourceVersion ?? "<none>"} bookmarks={(Bookmarks.HasValue ? Bookmarks.Value.ToString().ToLowerInvariant() : "<unset>")}";
        }

        public List<WatchRequest> Requests { get; } = [];

        public List<(string Name, int Version)> Sites { get; } = [("site-a", 10), ("site-b", 11)];

        public string? FailWith { get; init; }

        public void NewSession()
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var rv = string.IsNullOrEmpty(query["resourceVersion"]) ? null : query["resourceVersion"];
            bool? bookmarks = bool.TryParse(query["allowWatchBookmarks"], out var b) ? b : null;
            bool? sendInitial = bool.TryParse(query["sendInitialEvents"], out var s) ? s : null;
            Requests.Add(new WatchRequest(rv, bookmarks, sendInitial));

            var emptyOrZero = rv is null or "0";
            var refuse = refuseUnversionedWatch
                         && (rv is null || (emptyOrZero && bookmarks == true) || sendInitial == true);
            if (FailWith is not null || refuse)
            {
                var status = $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","message":"{{FailWith ?? RefusalMessage}}","reason":"InternalError","code":500}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = new StringContent(status, Encoding.UTF8, "application/json")
                });
            }

            // rv=0 / none: the whole current state as ADDED. Exact rv: only what happened after it.
            var after = emptyOrZero ? 0 : int.Parse(rv!);
            var body = new StringBuilder();
            foreach (var (name, version) in Sites.Where(site => site.Version > after))
            {
                body.AppendLine(
                    "{\"type\":\"ADDED\",\"object\":{\"apiVersion\":\"octo-mesh.meshmakers.io/v1\",\"kind\":\"DeploymentSite\","
                    + $"\"metadata\":{{\"name\":\"{name}\",\"namespace\":\"octo\",\"resourceVersion\":\"{version}\"}},"
                    + $"\"spec\":{{\"tenantId\":\"tenant\",\"deploymentSiteRtId\":\"{name}\"}}}}}}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
