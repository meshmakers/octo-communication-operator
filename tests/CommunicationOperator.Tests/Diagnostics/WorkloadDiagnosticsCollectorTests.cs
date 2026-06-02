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

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release);

        var result = sb.ToString();
        await Assert.That(result).Contains("Failed");
        await Assert.That(result).Contains("pull access denied");
    }

    [Test]
    public async Task FormatWarningEvents_EventForUnrelatedRelease_IsExcluded()
    {
        var sb = new StringBuilder();
        var events = new[]
        {
            Warning("other-release-pod-1", "Pod", "Failed", "not our problem"),
        };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release);

        await Assert.That(sb.ToString()).IsEmpty();
    }

    [Test]
    public async Task FormatWarningEvents_DuplicateEvents_AreDeduplicated()
    {
        var sb = new StringBuilder();
        var dup = Warning($"{Release}-pod-1", "Pod", "Failed", "image pull error");
        var events = new[] { dup, dup, dup };

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release);

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

        WorkloadDiagnosticsCollector.FormatWarningEvents(sb, events, Release);

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

    private static Corev1Event Warning(string objectName, string kind, string reason, string message) => new()
    {
        Metadata = new V1ObjectMeta { Name = $"{objectName}.evt" },
        InvolvedObject = new V1ObjectReference { Name = objectName, Kind = kind },
        Type = "Warning",
        Reason = reason,
        Message = message,
    };
}
