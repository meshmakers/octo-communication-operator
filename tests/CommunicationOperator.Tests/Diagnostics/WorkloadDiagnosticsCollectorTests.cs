using System.Text;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Diagnostics;

namespace Meshmakers.Octo.Communication.Operator.Tests.Diagnostics;

/// <summary>
/// Tests target the internal formatters directly so we don't have to mock
/// the verbose <c>IKubernetes</c> / <c>ICoreV1Operations</c> surface — the
/// raw API calls are thin pass-throughs documented in the collector and
/// exercised manually / via E2E.
/// </summary>
internal class WorkloadDiagnosticsCollectorTests
{
    private const string Release = "acme-65d5c447b420da3fb12381b1";

    [Test]
    public async Task FormatPodStates_NoPods_ProducesNothing()
    {
        var sb = new StringBuilder();

        WorkloadDiagnosticsCollector.FormatPodStates(sb, Array.Empty<V1Pod>());

        await Assert.That(sb.ToString()).IsEmpty();
    }

    [Test]
    public async Task FormatPodStates_ImagePullBackOff_IsReported()
    {
        var sb = new StringBuilder();
        var pods = new[]
        {
            Pod("pod-a", waiting: ("ImagePullBackOff",
                "Back-off pulling image \"meshmakers/voestalpine:0.3.0.0\"")),
        };

        WorkloadDiagnosticsCollector.FormatPodStates(sb, pods);

        var result = sb.ToString();
        await Assert.That(result).Contains("ImagePullBackOff");
        await Assert.That(result).Contains("Back-off pulling image");
        await Assert.That(result).Contains("pod-a");
    }

    [Test]
    public async Task FormatPodStates_BenignWaitingReasons_AreSuppressed()
    {
        var sb = new StringBuilder();
        var pods = new[]
        {
            Pod("pod-a", waiting: ("ContainerCreating", "")),
            Pod("pod-b", waiting: ("PodInitializing", "")),
        };

        WorkloadDiagnosticsCollector.FormatPodStates(sb, pods);

        await Assert.That(sb.ToString()).IsEmpty();
    }

    [Test]
    public async Task FormatPodStates_PreviousTerminatedNonZeroExit_IsReported()
    {
        var sb = new StringBuilder();
        var pods = new[]
        {
            Pod("pod-a", lastTerminated: (137, "OOMKilled", "Memory cgroup out of memory")),
        };

        WorkloadDiagnosticsCollector.FormatPodStates(sb, pods);

        var result = sb.ToString();
        await Assert.That(result).Contains("exit code 137");
        await Assert.That(result).Contains("OOMKilled");
    }

    [Test]
    public async Task FormatPodStates_PreviousTerminatedZeroExit_IsSuppressed()
    {
        var sb = new StringBuilder();
        var pods = new[]
        {
            Pod("pod-a", lastTerminated: (0, "Completed", "")),
        };

        WorkloadDiagnosticsCollector.FormatPodStates(sb, pods);

        await Assert.That(sb.ToString()).IsEmpty();
    }

    [Test]
    public async Task FormatPodStates_InitContainerWaiting_IsReportedAsInitContainer()
    {
        var sb = new StringBuilder();
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "pod-a" },
            Status = new V1PodStatus
            {
                InitContainerStatuses = new[]
                {
                    new V1ContainerStatus
                    {
                        Name = "init",
                        State = new V1ContainerState
                        {
                            Waiting = new V1ContainerStateWaiting
                            {
                                Reason = "CreateContainerConfigError",
                                Message = "secret \"db-creds\" not found",
                            },
                        },
                    },
                },
            },
        };

        WorkloadDiagnosticsCollector.FormatPodStates(sb, new[] { pod });

        var result = sb.ToString();
        await Assert.That(result).Contains("initContainer");
        await Assert.That(result).Contains("CreateContainerConfigError");
        await Assert.That(result).Contains("db-creds");
    }

    [Test]
    public async Task FormatWarningEvents_EventForRelease_IsIncluded()
    {
        var sb = new StringBuilder();
        var events = new[]
        {
            Warning($"{Release}-app-abc-xyz", "Pod", "Failed", "pull access denied"),
        };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release, Since);

        var result = sb.ToString();
        await Assert.That(result).Contains("Failed");
        await Assert.That(result).Contains("pull access denied");
    }

    /// <summary>
    ///     🔴 An event from a PREVIOUS rollout of the same release must not be reported.
    /// </summary>
    /// <remarks>
    ///     Events are matched to a release by name prefix, and Kubernetes keeps them for about an
    ///     hour — so every pod of every past attempt carries the same prefix and was still on
    ///     record. Without the cutoff a healthy deploy arrived in the UI as a page of
    ///     ImagePullBackOff and readiness failures from pods deleted 40 minutes earlier, which is
    ///     the exact opposite of what this collector is for: it exists to surface a live failure
    ///     within seconds, and it was making a live failure indistinguishable from the last hour's
    ///     noise. Observed on a local kind cluster.
    /// </remarks>
    [Test]
    public async Task FormatWarningEvents_EventFromAnEarlierRolloutOfTheSameRelease_IsExcluded()
    {
        var sb = new StringBuilder();
        var deployStarted = DateTime.UtcNow;
        var events = new[]
        {
            Warning($"{Release}-old-rs-deadpod", "Pod", "Failed", "ImagePullBackOff",
                lastTimestamp: deployStarted.AddMinutes(-40)),
            Warning($"{Release}-new-rs-livepod", "Pod", "Failed", "still broken",
                lastTimestamp: deployStarted.AddSeconds(5)),
        };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release, deployStarted);

        var result = sb.ToString();
        await Assert.That(result).DoesNotContain("ImagePullBackOff");
        await Assert.That(result).Contains("still broken");
    }

    /// <summary>
    ///     A repeating failure that STARTED before this deploy and is still going must be reported:
    ///     the cutoff reads the last occurrence, never the first.
    /// </summary>
    [Test]
    public async Task FormatWarningEvents_OngoingFailureThatStartedEarlier_IsIncluded()
    {
        var sb = new StringBuilder();
        var deployStarted = DateTime.UtcNow;
        var evt = Warning($"{Release}-pod", "Pod", "BackOff", "crash looping",
            lastTimestamp: deployStarted.AddSeconds(10));
        evt.FirstTimestamp = deployStarted.AddMinutes(-30);

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, new[] { evt }, Release, deployStarted);

        await Assert.That(sb.ToString()).Contains("crash looping");
    }

    /// <summary>
    ///     An event with no timestamp at all is kept. It cannot be proven old, and dropping it
    ///     would hide a live failure — the more expensive of the two mistakes here.
    /// </summary>
    [Test]
    public async Task FormatWarningEvents_EventWithoutAnyTimestamp_IsIncluded()
    {
        var sb = new StringBuilder();
        var evt = Warning($"{Release}-pod", "Pod", "Failed", "no timestamp");
        evt.LastTimestamp = null;

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, new[] { evt }, Release, DateTime.UtcNow);

        await Assert.That(sb.ToString()).Contains("no timestamp");
    }

    [Test]
    public async Task FormatWarningEvents_EventForUnrelatedRelease_IsExcluded()
    {
        var sb = new StringBuilder();
        var events = new[]
        {
            Warning("other-release-pod-1", "Pod", "Failed", "not our problem"),
        };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release, Since);

        await Assert.That(sb.ToString()).IsEmpty();
    }

    [Test]
    public async Task FormatWarningEvents_DuplicateEvents_AreDeduplicated()
    {
        var sb = new StringBuilder();
        var dup = Warning($"{Release}-pod-1", "Pod", "Failed", "image pull error");
        var events = new[] { dup, dup, dup };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release, Since);

        var occurrences = sb.ToString().Split('\n').Count(l => l.Contains("image pull error"));
        await Assert.That(occurrences).IsEqualTo(1);
    }

    [Test]
    public async Task FormatWarningEvents_NullInvolvedObjectName_IsSkipped()
    {
        var sb = new StringBuilder();
        var events = new[]
        {
            new Corev1Event
            {
                Metadata = new V1ObjectMeta { Name = "evt-1" },
                InvolvedObject = new V1ObjectReference { Kind = "Pod", Name = null },
                Type = "Warning",
                Reason = "Failed",
                Message = "irrelevant",
            },
        };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release, Since);

        await Assert.That(sb.ToString()).IsEmpty();
    }

    private static V1Pod Pod(string name,
        (string Reason, string Message)? waiting = null,
        (int ExitCode, string Reason, string Message)? lastTerminated = null)
    {
        var status = new V1ContainerStatus { Name = "app" };
        if (waiting.HasValue)
        {
            status.State = new V1ContainerState
            {
                Waiting = new V1ContainerStateWaiting
                {
                    Reason = waiting.Value.Reason,
                    Message = waiting.Value.Message,
                },
            };
        }
        if (lastTerminated.HasValue)
        {
            status.LastState = new V1ContainerState
            {
                Terminated = new V1ContainerStateTerminated
                {
                    ExitCode = lastTerminated.Value.ExitCode,
                    Reason = lastTerminated.Value.Reason,
                    Message = lastTerminated.Value.Message,
                },
            };
        }

        return new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name },
            Status = new V1PodStatus { ContainerStatuses = new[] { status } },
        };
    }

    private static Corev1Event Warning(string objectName, string kind, string reason, string message,
        DateTime? lastTimestamp = null) => new()
    {
        Metadata = new V1ObjectMeta { Name = $"{objectName}.evt" },
        InvolvedObject = new V1ObjectReference { Name = objectName, Kind = kind },
        Type = "Warning",
        Reason = reason,
        Message = message,
        // Default: happened now, i.e. inside any cutoff a test passes. The age cases set it.
        LastTimestamp = lastTimestamp ?? DateTime.UtcNow,
    };

    /// <summary>A cutoff comfortably before every "now" event the factory produces.</summary>
    private static DateTime Since => DateTime.UtcNow.AddMinutes(-1);
}
