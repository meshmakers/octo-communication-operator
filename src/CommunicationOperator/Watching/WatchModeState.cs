using Meshmakers.Octo.Communication.Operator.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Watching;

/// <summary>
///     Process-wide switch between the two ways the operator starts a custom-resource watch (AB#6898).
///     Shared by every watcher, because the cause - an API server without etcd watch progress notifications -
///     is a property of the cluster, not of one resource type.
/// </summary>
public sealed class WatchModeState
{
    private readonly ILogger<WatchModeState> _logger;
    private int _classic;

    public WatchModeState(ILogger<WatchModeState> logger, IOptions<OperatorOptions> options)
    {
        _logger = logger;
        if (options.Value.ForceClassicWatch)
        {
            _classic = 1;
            _logger.LogWarning(
                "Classic list+watch is forced by configuration (OPERATOR__FORCECLASSICWATCH): watches start at resourceVersion=0 instead of a consistent read.");
        }
    }

    /// <summary>True once watches must start at <c>resourceVersion=0</c>.</summary>
    public bool IsClassic => Volatile.Read(ref _classic) == 1;

    /// <summary>
    ///     Switches to classic mode. Returns true for the caller that made the switch, which is the only
    ///     one that logs - the WARN appears once per process, not once per retry.
    /// </summary>
    public bool SwitchToClassic(Exception refusal)
    {
        if (Interlocked.Exchange(ref _classic, 1) == 1)
        {
            return false;
        }

        _logger.LogWarning(
            "The API server refused the watch ({Reason}); switching all watchers to classic list+watch (resourceVersion=0). "
            + "This is a cluster-side condition (etcd RequestWatchProgress disabled); the operator keeps working without it.",
            refusal.Message);
        return true;
    }
}
