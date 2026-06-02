using System.Text;
using k8s;
using k8s.Models;

namespace Meshmakers.Octo.Communication.Operator.Diagnostics;

/// <summary>
/// Default <see cref="IWorkloadDiagnosticsCollector"/>. Queries the cluster
/// for failure-relevant signals on the release's pods and events. The
/// implementation is deliberately defensive: any sub-query that fails
/// (network blip, RBAC denial, race with pod deletion) is logged and
/// skipped — the collector still returns whatever it managed to capture
/// so the caller can surface partial diagnostics rather than nothing.
/// </summary>
public sealed class WorkloadDiagnosticsCollector : IWorkloadDiagnosticsCollector
{
    /// <summary>
    /// Container <c>waiting</c> reasons that are normal during pod startup
    /// and should not be surfaced as failures. Anything else (e.g.
    /// <c>ImagePullBackOff</c>, <c>ErrImagePull</c>, <c>CrashLoopBackOff</c>,
    /// <c>CreateContainerConfigError</c>, <c>InvalidImageName</c>) is
    /// reported verbatim.
    /// </summary>
    private static readonly HashSet<string> BenignWaitingReasons = new(StringComparer.Ordinal)
    {
        "PodInitializing",
        "ContainerCreating",
    };

    private readonly IKubernetes _kubernetes;
    private readonly ILogger<WorkloadDiagnosticsCollector> _logger;

    public WorkloadDiagnosticsCollector(IKubernetes kubernetes, ILogger<WorkloadDiagnosticsCollector> logger)
    {
        _kubernetes = kubernetes;
        _logger = logger;
    }

    public async Task<string> CollectAsync(string @namespace, string release, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        var pods = await ListPodsAsync(@namespace, release, cancellationToken);
        if (pods != null)
        {
            FormatPodStates(sb, pods);
        }

        var events = await ListWarningEventsAsync(@namespace, cancellationToken);
        if (events != null)
        {
            FormatWarningEvents(sb, events, release);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Pure formatter for pod container states. Internal so tests can drive
    /// it without mocking IKubernetes.
    /// </summary>
    internal static void FormatPodStates(StringBuilder sb, IEnumerable<V1Pod> pods)
    {
        foreach (var pod in pods)
        {
            var podName = pod.Metadata?.Name ?? "<unknown>";
            AppendContainerStates(sb, podName, "container", pod.Status?.ContainerStatuses);
            AppendContainerStates(sb, podName, "initContainer", pod.Status?.InitContainerStatuses);
        }
    }

    /// <summary>
    /// Pure formatter for warning events whose involvedObject name starts
    /// with the release name (covers Deployment, ReplicaSet, Pod, Service,
    /// Ingress that helm names from the release). Internal so tests can
    /// drive it without mocking IKubernetes.
    /// </summary>
    internal static void FormatWarningEvents(StringBuilder sb, IEnumerable<Corev1Event> events, string release)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in events)
        {
            var objName = evt.InvolvedObject?.Name;
            if (objName == null || !objName.StartsWith(release, StringComparison.Ordinal))
            {
                continue;
            }

            var line = FormatEvent(evt);
            if (seen.Add(line))
            {
                sb.AppendLine(line);
            }
        }
    }

    private static void AppendContainerStates(StringBuilder sb, string podName, string kind,
        IList<V1ContainerStatus>? statuses)
    {
        if (statuses == null) return;

        foreach (var status in statuses)
        {
            var name = status.Name ?? "<unknown>";

            var waiting = status.State?.Waiting;
            if (waiting?.Reason is { Length: > 0 } reason && !BenignWaitingReasons.Contains(reason))
            {
                sb.Append("Pod ").Append(podName).Append(' ').Append(kind).Append(" '").Append(name)
                  .Append("' waiting: ").Append(reason);
                if (!string.IsNullOrWhiteSpace(waiting.Message))
                {
                    sb.Append(" — ").Append(waiting.Message.Trim());
                }
                sb.AppendLine();
            }

            var lastTerminated = status.LastState?.Terminated;
            if (lastTerminated is { ExitCode: not 0 })
            {
                sb.Append("Pod ").Append(podName).Append(' ').Append(kind).Append(" '").Append(name)
                  .Append("' previously terminated: exit code ").Append(lastTerminated.ExitCode);
                if (!string.IsNullOrWhiteSpace(lastTerminated.Reason))
                {
                    sb.Append(" (").Append(lastTerminated.Reason).Append(')');
                }
                if (!string.IsNullOrWhiteSpace(lastTerminated.Message))
                {
                    sb.Append(" — ").Append(lastTerminated.Message.Trim());
                }
                sb.AppendLine();
            }
        }
    }

    private static string FormatEvent(Corev1Event evt)
    {
        var kind = evt.InvolvedObject?.Kind ?? "<unknown>";
        var name = evt.InvolvedObject?.Name ?? "<unknown>";
        var reason = evt.Reason ?? "<no-reason>";
        var message = evt.Message?.Trim() ?? string.Empty;
        return $"Event {kind}/{name}: {reason} — {message}";
    }

    private async Task<IList<V1Pod>?> ListPodsAsync(string @namespace, string release, CancellationToken cancellationToken)
    {
        try
        {
            var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
                @namespace,
                labelSelector: $"app.kubernetes.io/instance={release}",
                cancellationToken: cancellationToken);
            return pods.Items;
        }
        catch (Exception ex)
        {
            // Pod list might be unavailable for legitimate reasons (atomic
            // rollback already cleaned everything up, RBAC). Don't fail the
            // collector — events alone are often enough.
            _logger.LogDebug(ex, "Diagnostics: pod list failed for release '{Release}' in '{Namespace}'", release, @namespace);
            return null;
        }
    }

    private async Task<IList<Corev1Event>?> ListWarningEventsAsync(string @namespace, CancellationToken cancellationToken)
    {
        try
        {
            var events = await _kubernetes.CoreV1.ListNamespacedEventAsync(
                @namespace,
                fieldSelector: "type=Warning",
                cancellationToken: cancellationToken);
            return events.Items;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnostics: event list failed in '{Namespace}'", @namespace);
            return null;
        }
    }
}
