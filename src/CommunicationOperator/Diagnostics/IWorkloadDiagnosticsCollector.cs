namespace Meshmakers.Octo.Communication.Operator.Diagnostics;

/// <summary>
/// Collects pod-level failure context for a Helm release so that an opaque
/// helm error like <c>context deadline exceeded</c> (the only signal
/// <c>helm upgrade --install --atomic</c> emits when its wait window
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
    /// <param name="cancellationToken">Token; intentionally honored even on
    /// failure paths so the caller can bound how long diagnostics waits.</param>
    Task<string> CollectAsync(string @namespace, string release, CancellationToken cancellationToken);
}
