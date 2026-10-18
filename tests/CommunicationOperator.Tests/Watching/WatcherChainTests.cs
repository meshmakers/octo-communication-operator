using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Builder;
using KubeOps.KubernetesClient;
using KubeOps.Operator;
using Meshmakers.Octo.Communication.Operator.Controller;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Communication.Operator.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Watching;

// AB#6898 - the whole chain with the REAL KubeOps ResourceWatcher: the DI container hands the watcher the
// WatchFallbackKubernetesClient, the watcher starts against a fake API server that follows the prod-1 matrix,
// the DeploymentSiteController reconciles each site and registers it at the controller (the step that reports the
// site online). PR #34's unit tests did not cover this chain and let the bookmarks mistake through.
public class WatcherChainTests
{
    [Test]
    public async Task Restarted_operator_learns_its_sites_from_a_prod_1_like_api_server_and_registers_them()
    {
        var api = new ProdLikeApiServer();
        var service = Substitute.For<IDeploymentSiteService>();
        var registered = new TaskCompletionSource();
        var names = new List<string>();
        service.RegisterDeploymentSiteAsync(Arg.Any<V1DeploymentSiteEntity>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (names)
                {
                    names.Add(call.Arg<V1DeploymentSiteEntity>().Metadata.Name);
                    if (names.Distinct().Count() == 2)
                    {
                        registered.TrySetResult();
                    }
                }

                return Task.CompletedTask;
            });

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKubernetesOperator().AddController<DeploymentSiteController, V1DeploymentSiteEntity>();
        builder.Services.AddSingleton(service);
        // Same registrations as Program.cs.
        builder.Services.Configure<OperatorOptions>(_ => { });
        builder.Services.AddSingleton<WatchModeState>();
        builder.Services.AddSingleton<IKubernetesClient>(sp => new WatchFallbackKubernetesClient(
            new KubernetesClientConfiguration { Host = "http://fake-api-server" },
            new Kubernetes(new KubernetesClientConfiguration { Host = "http://fake-api-server" }, api),
            sp.GetRequiredService<WatchModeState>()));

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IKubernetesClient>();
        await Assert.That(client).IsTypeOf<WatchFallbackKubernetesClient>();

        await host.StartAsync();
        try
        {
            var done = await Task.WhenAny(registered.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            await Assert.That(done).IsEqualTo(registered.Task);
        }
        finally
        {
            await host.StopAsync();
        }

        await Assert.That(host.Services.GetRequiredService<WatchModeState>().IsClassic).IsTrue();
        await Assert.That(api.Refused).IsGreaterThanOrEqualTo(1);
        await Assert.That(api.Watches.Any(w => w == "rv=0 bookmarks=false")).IsTrue();
    }

    // prod-1 matrix for the DeploymentSite watch; every other call (status updates) is answered with the body.
    private sealed class ProdLikeApiServer : DelegatingHandler
    {
        private int _refused;

        public int Refused => _refused;

        public List<string> Watches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (query["watch"] is not "true" && query["watch"] is not "1")
            {
                // Status update: answer with the stored object, named by the request path (.../deploymentsites/<name>/status).
                var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var name = segments[Array.IndexOf(segments, "deploymentsites") + 1];
                var content =
                    "{\"apiVersion\":\"octo-mesh.meshmakers.io/v1\",\"kind\":\"DeploymentSite\","
                    + $"\"metadata\":{{\"name\":\"{name}\",\"namespace\":\"octo\",\"resourceVersion\":\"99\",\"uid\":\"uid-{name}\"}},"
                    + $"\"spec\":{{\"tenantId\":\"tenant\",\"deploymentSiteRtId\":\"{name}\"}},\"status\":{{}}}}";
                await Task.CompletedTask;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                };
            }

            var rv = string.IsNullOrEmpty(query["resourceVersion"]) ? null : query["resourceVersion"];
            var bookmarks = query["allowWatchBookmarks"];
            lock (Watches)
            {
                Watches.Add($"rv={rv ?? "<none>"} bookmarks={(bookmarks is null ? "<unset>" : bookmarks.ToLowerInvariant())}");
            }

            if (rv is null || (rv == "0" && bookmarks?.ToLowerInvariant() == "true"))
            {
                Interlocked.Increment(ref _refused);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"a watch stream was requested by the client but the required storage feature RequestWatchProgress is disabled","reason":"InternalError","code":500}""",
                        Encoding.UTF8, "application/json")
                };
            }

            var body = new StringBuilder();
            foreach (var (name, version) in new[] { ("site-a", "10"), ("site-b", "11") })
            {
                body.AppendLine(
                    "{\"type\":\"ADDED\",\"object\":{\"apiVersion\":\"octo-mesh.meshmakers.io/v1\",\"kind\":\"DeploymentSite\","
                    + $"\"metadata\":{{\"name\":\"{name}\",\"namespace\":\"octo\",\"resourceVersion\":\"{version}\",\"uid\":\"uid-{name}\"}},"
                    + $"\"spec\":{{\"tenantId\":\"tenant\",\"deploymentSiteRtId\":\"{name}\"}}}}}}");
            }

            // Keep the stream open like a real watch, so the watcher does not reconnect in a loop.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new HoldOpenStream(Encoding.UTF8.GetBytes(body.ToString())))
            };
        }
    }

    // Serves the bytes, then blocks until cancelled/disposed.
    private sealed class HoldOpenStream(byte[] data) : Stream
    {
        private int _position;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < data.Length)
            {
                var n = Math.Min(buffer.Length, data.Length - _position);
                data.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            try
            {
                await _closed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
