using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers;

internal class WorkloadDeployWatcherTests
{
    private const string Namespace = "octo";
    private const string Release = "acme-65d5c447b420da3fb12381b1";
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);

    private static WorkloadDeployedDto Dto() => new()
    {
        TenantId = "acme",
        PoolRtId = "65d5c447b420da3fb12381a1",
        WorkloadRtId = "65d5c447b420da3fb12381b1",
        WorkloadName = "voest-app",
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://example.invalid",
        ChartName = "voest-app",
        ChartVersion = "1.0.0",
        ValuesYaml = string.Empty,
        Values = Array.Empty<ValueOverrideDto>(),
    };

    private static (IWorkloadDiagnosticsCollector collector, IOperatorHubInvoker hub) BuildMocks(string returns)
    {
        var collector = Substitute.For<IWorkloadDiagnosticsCollector>();
        collector.CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(returns);
        var hub = Substitute.For<IOperatorHubInvoker>();
        return (collector, hub);
    }

    [Test]
    public async Task RunAsync_NonEmptyDiagnostic_PublishesProgressOnce()
    {
        var (collector, hub) = BuildMocks("Pod foo waiting: ImagePullBackOff");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        await hub.Received().ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p =>
                p.TenantId == "acme"
                && p.WorkloadRtId == "65d5c447b420da3fb12381b1"
                && p.Message == "Pod foo waiting: ImagePullBackOff"));
    }

    [Test]
    public async Task RunAsync_EmptyDiagnostic_DoesNotPublish()
    {
        var (collector, hub) = BuildMocks(string.Empty);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        await hub.DidNotReceiveWithAnyArgs()
            .ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>());
    }

    [Test]
    public async Task RunAsync_RepeatedIdenticalDiagnostic_PublishedOnlyOnce()
    {
        var collector = Substitute.For<IWorkloadDiagnosticsCollector>();
        var collectedEnough = new TaskCompletionSource();
        var collectCount = 0;
        collector.CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Feed the SAME snapshot several times so the dedup path is
                // actually exercised (it must publish once, then suppress the
                // repeats), then release the test.
                if (Interlocked.Increment(ref collectCount) >= 3)
                {
                    collectedEnough.TrySetResult();
                }

                return "Pod foo waiting: ImagePullBackOff";
            });
        var hub = Substitute.For<IOperatorHubInvoker>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        // Wait until the loop has collected the identical diagnostic several
        // times (dedup had multiple chances to wrongly re-publish), THEN cancel
        // and assert. Replaces the flaky "cancel after a fixed 250 ms" pattern
        // the sibling tests already moved off — on a slower CI agent the loop
        // could produce zero (not one) publishes inside the fixed window, which
        // is exactly how this test failed the first run of build 39521.
        await collectedEnough.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        await hub.Received(1).ReportWorkloadDeploymentProgressAsync(
            Arg.Any<WorkloadDeploymentProgressDto>());
    }

    [Test]
    public async Task RunAsync_DiagnosticChanges_PublishesEachNewSnapshot()
    {
        var collector = Substitute.For<IWorkloadDiagnosticsCollector>();
        var snapshots = new Queue<string>(new[] { "first", "first", "second", "second", "third" });
        collector.CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => snapshots.Count > 0 ? snapshots.Dequeue() : "third");
        var hub = Substitute.For<IOperatorHubInvoker>();
        var publishedThree = new TaskCompletionSource();
        var publishCount = 0;
        hub.When(h => h.ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>()))
            .Do(_ =>
            {
                if (Interlocked.Increment(ref publishCount) >= 3)
                {
                    publishedThree.TrySetResult();
                }
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        // Wait until the watcher has published all three distinct snapshots,
        // THEN cancel. Avoids the flaky "cancel after fixed ms" pattern that
        // raced the polling loop on slower CI agents.
        await publishedThree.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        await hub.Received(1).ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p => p.Message == "first"));
        await hub.Received(1).ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p => p.Message == "second"));
        await hub.Received(1).ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p => p.Message == "third"));
    }

    [Test]
    public async Task RunAsync_CollectorThrows_KeepsRunningAndPublishesNextSuccessfulSnapshot()
    {
        var collector = Substitute.For<IWorkloadDiagnosticsCollector>();
        collector.CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("apiserver glitch"),
                _ => "Pod foo waiting: ImagePullBackOff");
        var hub = Substitute.For<IOperatorHubInvoker>();
        var published = new TaskCompletionSource();
        hub.When(h => h.ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>()))
            .Do(_ => published.TrySetResult());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        // Block until the second iteration has surfaced the snapshot through
        // the hub. The first iteration's exception is swallowed and the loop
        // must continue — cancelling after a fixed ms window was too tight
        // on slower CI agents where the two iterations can't both complete
        // inside 200ms.
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        await hub.Received().ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p => p.Message == "Pod foo waiting: ImagePullBackOff"));
    }

    [Test]
    public async Task RunAsync_HubThrows_LoopContinues()
    {
        var (collector, hub) = BuildMocks("Pod foo waiting: ImagePullBackOff");
        var publishAttempted = new TaskCompletionSource();
        hub.When(h => h.ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>()))
            .Do(_ =>
            {
                publishAttempted.TrySetResult();
                throw new InvalidOperationException("hub blew up");
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        // Synchronize on the first publish attempt rather than a fixed wall-clock
        // budget — 150 ms was too tight on slower CI agents (same flake the
        // sibling tests already fixed). Reaching the end without an unhandled
        // exception is the real assertion; the Received() check below just
        // confirms the loop got at least one publish call out.
        await publishAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await run;

        await hub.Received().ReportWorkloadDeploymentProgressAsync(
            Arg.Any<WorkloadDeploymentProgressDto>());
    }

    [Test]
    public async Task RunAsync_PreCancelledToken_ReturnsImmediately()
    {
        var (collector, hub) = BuildMocks("Pod foo waiting: ImagePullBackOff");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        await collector.DidNotReceiveWithAnyArgs()
            .CollectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await hub.DidNotReceiveWithAnyArgs()
            .ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>());
    }
}
