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

// AB#6898 - the API server of prod-1 refused every watch that starts without a resourceVersion with
// "a watch stream was requested by the client but the required storage feature RequestWatchProgress is
// disabled" (HTTP 500). KubeOps' ResourceWatcher retried that identical request forever, so a restarted
// operator pod never learned its DeploymentSites. These tests run the real k8s client against a fake API
// server that behaves like that cluster.
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
        // First the refused request without resourceVersion, then the classic one at resourceVersion=0.
        await Assert.That(server.ResourceVersions.ToArray()).IsEquivalentTo(new string?[] { null, "0" });
        await Assert.That(state.IsClassic).IsTrue();
    }

    [Test]
    public async Task Fallback_is_remembered_so_later_watches_skip_the_refused_request_and_warn_only_once()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: true);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        await Drain(client.WatchAsync<V1DeploymentSiteEntity>());
        await Drain(client.WatchAsync<V1DeploymentSiteEntity>());
        await Drain(client.WatchAsync<V1DeploymentSiteEntity>());

        // 1 refused + 3 classic. The retries of KubeOps' watcher no longer repeat the refused request.
        await Assert.That(server.ResourceVersions.Count(v => v is null)).IsEqualTo(1);
        await Assert.That(server.ResourceVersions.Count(v => v == "0")).IsEqualTo(3);
        await Assert.That(logger.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Healthy_api_server_keeps_the_default_watch()
    {
        using var server = new FakeApiServer(refuseUnversionedWatch: false);
        var logger = new CapturingLogger<WatchModeState>();
        var state = new WatchModeState(logger, MsOptions.Create(new OperatorOptions()));
        using var client = CreateClient(server, state);

        var events = await Drain(client.WatchAsync<V1DeploymentSiteEntity>());

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(server.ResourceVersions.ToArray()).IsEquivalentTo(new string?[] { null });
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

        var events = await Drain(client.WatchAsync<V1DeploymentSiteEntity>());

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(server.ResourceVersions.ToArray()).IsEquivalentTo(new string?[] { "0" });
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

        await Drain(client.WatchAsync<V1DeploymentSiteEntity>(resourceVersion: "4711"));

        await Assert.That(server.ResourceVersions.ToArray()).IsEquivalentTo(new string?[] { "4711" });
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
        await Assert.That(server.ResourceVersions.Count).IsEqualTo(1);
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

    // Behaves like the prod-1 API server: a watch without resourceVersion is refused with HTTP 500, a watch with
    // a resourceVersion streams the current state as ADDED events.
    private sealed class FakeApiServer(bool refuseUnversionedWatch) : DelegatingHandler
    {
        public List<string?> ResourceVersions { get; } = [];

        public string? FailWith { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            ResourceVersions.Add(string.IsNullOrEmpty(query["resourceVersion"]) ? null : query["resourceVersion"]);

            if (FailWith is not null || (refuseUnversionedWatch && string.IsNullOrEmpty(query["resourceVersion"])))
            {
                var status = $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","message":"{{FailWith ?? RefusalMessage}}","reason":"InternalError","code":500}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = new StringContent(status, Encoding.UTF8, "application/json")
                });
            }

            var body = new StringBuilder();
            foreach (var (name, version) in new[] { ("site-a", "10"), ("site-b", "11") })
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
