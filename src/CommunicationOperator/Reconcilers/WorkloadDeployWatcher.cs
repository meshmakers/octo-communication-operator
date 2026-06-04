using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Services;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Background loop that polls the cluster for failure-relevant signals on
/// a workload's pods / events while <c>helm upgrade --install --atomic</c>
/// is running, and pushes any new diagnostic snapshot to the controller via
/// <see cref="IOperatorHubInvoker.ReportWorkloadDeploymentProgressAsync"/>.
///
/// Without this watcher the user would only see the failure cause after
/// helm's atomic timeout elapsed (default 5 min) — by then the operator
/// reports a terminal <c>Error</c>. The watcher closes that gap to ~3s.
///
/// Lifecycle:
/// <list type="bullet">
/// <item><see cref="WorkloadReconciler.DeployAsync"/> starts a task with
///   <see cref="RunAsync"/> right before invoking <c>helm upgrade --install</c>.</item>
/// <item>The task runs until cancellation, then returns. The reconciler
///   cancels + awaits it in its <c>finally</c>, so the watcher is
///   guaranteed to be stopped by the time the terminal status report runs.</item>
/// <item>Collector or reporter exceptions are logged but do not stop the
///   loop — a transient apiserver glitch must not silently disable
///   feedback for the rest of the deploy.</item>
/// </list>
/// </summary>
public static class WorkloadDeployWatcher
{
    /// <summary>Default poll cadence used in production. Overridable per
    /// call so tests can drive the loop at millisecond speeds.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Bounded collector timeout per tick; deliberately short
    /// so a stuck apiserver does not stall the polling cadence.</summary>
    internal static readonly TimeSpan CollectTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(
        IWorkloadDiagnosticsCollector collector,
        IOperatorHubInvoker hub,
        string @namespace,
        string release,
        WorkloadDeployedDto workload,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        string? lastSent = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                string snapshot;
                using (var collectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    collectCts.CancelAfter(CollectTimeout);
                    try
                    {
                        snapshot = await collector.CollectAsync(@namespace, release, collectCts.Token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex,
                            "Diagnostics poll failed for release '{Release}'; will retry on next tick",
                            release);
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(snapshot) || string.Equals(snapshot, lastSent, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
                    {
                        TenantId = workload.TenantId,
                        WorkloadName = workload.WorkloadName,
                        WorkloadRtId = workload.WorkloadRtId,
                        Message = snapshot,
                    });
                    lastSent = snapshot;
                }
                catch (Exception ex)
                {
                    // Hub invoker already swallows known transient cases; this
                    // catch is the last line of defense so a runaway exception
                    // can never escape into the reconciler's deploy path.
                    logger.LogDebug(ex,
                        "Failed to publish progress for release '{Release}'", release);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation through the outer token — normal end-of-deploy.
        }
    }
}
