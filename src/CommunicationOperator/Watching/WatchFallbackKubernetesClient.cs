using System.Runtime.CompilerServices;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.KubernetesClient;

namespace Meshmakers.Octo.Communication.Operator.Watching;

/// <summary>
///     KubeOps <see cref="KubernetesClient" /> whose watch survives an API server that cannot serve a consistent
///     watch start (AB#6898).
/// </summary>
/// <remarks>
///     KubeOps' <c>ResourceWatcher</c> starts every watch without a <c>resourceVersion</c>. The API server answers
///     that with a consistent read from etcd, which needs the optional etcd feature <c>RequestWatchProgress</c>;
///     without it the request is refused (HTTP 500) and the watcher retries the identical request every ~33 s,
///     so a freshly started operator pod never learns its DeploymentSites. This client catches that refusal,
///     switches the process to classic mode once (<see cref="WatchModeState" />) and restarts the watch at
///     <c>resourceVersion=0</c>, which the API server serves from its watch cache: the current state as
///     <c>ADDED</c> events followed by the live changes and bookmarks. Reconnects that carry a bookmark
///     resource version are untouched. Only <see cref="WatchAsync{TEntity}" /> is replaced; the interface is
///     re-implemented so the rest is inherited.
/// </remarks>
public sealed class WatchFallbackKubernetesClient : KubernetesClient, IKubernetesClient
{
    private const string ClassicResourceVersion = "0";

    private readonly WatchModeState _state;

    public WatchFallbackKubernetesClient(WatchModeState state)
    {
        _state = state;
    }

    public WatchFallbackKubernetesClient(KubernetesClientConfiguration clientConfig, IKubernetes client, WatchModeState state)
        : base(clientConfig, client)
    {
        _state = state;
    }

    public new async IAsyncEnumerable<(WatchEventType, TEntity)> WatchAsync<TEntity>(
        string? @namespace = null,
        string? resourceVersion = null,
        string? labelSelector = null,
        string? fieldSelector = null,
        bool? allowWatchBookmarks = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TEntity : IKubernetesObject<V1ObjectMeta>
    {
        var startsUnversioned = string.IsNullOrEmpty(resourceVersion);

        while (true)
        {
            var version = startsUnversioned && _state.IsClassic ? ClassicResourceVersion : resourceVersion;
            await using var events = base
                .WatchAsync<TEntity>(@namespace, version, labelSelector, fieldSelector, allowWatchBookmarks, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            var delivered = false;
            var retryClassic = false;
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await events.MoveNextAsync();
                }
                catch (Exception e) when (!delivered && startsUnversioned && !_state.IsClassic && WatchRefusal.IsRefusal(e))
                {
                    _state.SwitchToClassic(e);
                    retryClassic = true;
                    break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                delivered = true;
                yield return events.Current;
            }

            if (!retryClassic)
            {
                yield break;
            }
        }
    }
}
