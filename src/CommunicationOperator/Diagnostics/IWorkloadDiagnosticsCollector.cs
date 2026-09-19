namespace Meshmakers.Octo.Communication.Operator.Diagnostics;

/// <summary>
/// Collects pod-level failure context for a Helm release so that an opaque
/// helm error like <c>context deadline exceeded</c> (the only signal
/// <c>helm upgrade --install --rollback-on-failure</c> emits when its wait window
/// elapses) can be enriched with the actual root cause: image-pull
/// failures, scheduling errors, crash loops, etc.
/// </summary>
public interface IWorkloadDiagnosticsCollector
{
    /// <summary>
    /// Snapshots failure-relevant signals for the given release. Returns an
    /// empty string when nothing notable is observed.
    /// </summary>
    /// <param name="namespace">Kubernetes namespace the release was deployed into.</param>
    /// <param name="release">Helm release name; doubles as the
    /// <c>app.kubernetes.io/instance</c> label and resource-name prefix.</param>
    /// <param name="sinceUtc">
    /// 🔴 Only warning events whose LAST occurrence is at or after this instant are reported —
    /// pass the moment this deploy began.
    ///
    /// Kubernetes keeps events for about an hour (<c>--event-ttl</c>), and they are matched to a
    /// release by name prefix, so without a cutoff every deploy re-reports the whole previous hour
    /// of that release's failures as if they were happening now. A successful deploy then arrives
    /// in the UI as a page of ImagePullBackOff and readiness failures from pods that were deleted
    /// long ago — which is the exact opposite of what this collector exists for, and makes a real
    /// failure indistinguishable from the noise. Observed on a local kind cluster after two earlier
    /// failed attempts: a healthy rollout reported both of their pods as broken.
    ///
    /// Pod states are not filtered: they are read from live pods and are current by construction.
    /// </param>
    /// <param name="cancellationToken">Token; intentionally honored even on
    /// failure paths so the caller can bound how long diagnostics waits.</param>
    Task<string> CollectAsync(string @namespace, string release, DateTime sinceUtc,
        CancellationToken cancellationToken);
}
