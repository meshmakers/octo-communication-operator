using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Diagnostics;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
        var (collector, hub) = BuildMocks("Pod foo waiting: ImagePullBackOff");
        // Cancellation after several ticks would otherwise produce N reports;
        // dedup must keep the count at exactly one.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        await hub.Received().ReportWorkloadDeploymentProgressAsync(
            Arg.Is<WorkloadDeploymentProgressDto>(p => p.Message == "Pod foo waiting: ImagePullBackOff"));
    }

    [Test]
    public async Task RunAsync_HubThrows_LoopContinues()
    {
        var (collector, hub) = BuildMocks("Pod foo waiting: ImagePullBackOff");
        hub.When(h => h.ReportWorkloadDeploymentProgressAsync(Arg.Any<WorkloadDeploymentProgressDto>()))
            .Throw(new InvalidOperationException("hub blew up"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        // Reaching the end without an unhandled exception is the assertion.
        await WorkloadDeployWatcher.RunAsync(collector, hub, Namespace, Release, Dto(),
            NullLogger.Instance, cts.Token, pollInterval: TickInterval);

        // Did at least attempt to publish.
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
