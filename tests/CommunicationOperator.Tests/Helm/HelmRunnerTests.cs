using Meshmakers.Octo.Communication.Operator.Helm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Helm;

internal class HelmRunnerTests
{
    private readonly IHelmProcessInvoker _invoker = Substitute.For<IHelmProcessInvoker>();
    private readonly HelmRunner _runner;

    public HelmRunnerTests()
    {
        _runner = new HelmRunner(_invoker, NullLogger<HelmRunner>.Instance);
        // Default: success.
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0, string.Empty, string.Empty));
    }

    [Test]
    public async Task EnsureRepoAsync_WithoutAuth_RunsRepoAddAndUpdate()
    {
        await _runner.EnsureRepoAsync("acme", "https://acme.github.io/charts", null, null, CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[] { "repo", "add", "acme", "https://acme.github.io/charts", "--force-update" })),
            Arg.Any<CancellationToken>());
        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[] { "repo", "update", "acme" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureRepoAsync_WithCredentials_AddsUsernamePasswordFlags()
    {
        await _runner.EnsureRepoAsync("priv", "https://priv.example.com/charts", "octo-bot", "secret-pat",
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "repo", "add", "priv", "https://priv.example.com/charts", "--force-update",
                    "--username", "octo-bot", "--password", "secret-pat",
                })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureRepoAsync_RepoAddFails_ThrowsHelmException()
    {
        _invoker.InvokeAsync(Arg.Is<IReadOnlyList<string>>(a => a[0] == "repo" && a[1] == "add"),
                Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(1, "stdout", "Error: failed to fetch"));

        await Assert.That(async () => await _runner.EnsureRepoAsync("acme", "https://nope", null, null,
            CancellationToken.None))
            .Throws<HelmException>();
    }

    [Test]
    public async Task UpgradeInstallAsync_BuildsExpectedArgs()
    {
        await _runner.UpgradeInstallAsync("acme-app", "acme/voest-app", "1.2.3", "octo",
            valuesFiles: new[] { "/tmp/values-a.yaml", "/tmp/values-b.yaml" },
            setValues: new Dictionary<string, string> { ["image.tag"] = "dev" },
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "upgrade", "--install", "acme-app", "acme/voest-app",
                    "--version", "1.2.3",
                    "--namespace", "octo",
                    "--atomic",
                    "-f", "/tmp/values-a.yaml",
                    "-f", "/tmp/values-b.yaml",
                    "--set", "image.tag=dev",
                })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpgradeInstallAsync_EmptyVersion_OmitsVersionFlag()
    {
        // System.Communication.MainLatest seeds an empty ChartVersion on dev/test
        // tenants so the first Deploy resolves to the newest chart in the configured
        // repo. helm rejects --version with an empty value, so the flag is dropped
        // entirely; helm's default behaviour then picks the highest semver tag.
        await _runner.UpgradeInstallAsync("rel", "acme/app", string.Empty, "octo",
            valuesFiles: Array.Empty<string>(),
            setValues: new Dictionary<string, string>(),
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "upgrade", "--install", "rel", "acme/app",
                    "--namespace", "octo",
                    "--atomic",
                })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpgradeInstallAsync_WhitespaceVersion_OmitsVersionFlag()
    {
        // Defensive: a CK attribute carrying "  " (e.g. a yaml >- block that collapsed
        // to whitespace) should be treated the same as empty rather than passed to helm
        // verbatim, which would fail with "invalid version constraint".
        await _runner.UpgradeInstallAsync("rel", "acme/app", "   ", "octo",
            valuesFiles: Array.Empty<string>(),
            setValues: new Dictionary<string, string>(),
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a => !a.Contains("--version")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpgradeInstallAsync_EscapesCommasAndEquals()
    {
        // helm uses comma and = as --set separators; characters in the value
        // need backslash escaping so the entire payload reaches the chart.
        await _runner.UpgradeInstallAsync("rel", "acme/app", "1.0.0", "octo",
            valuesFiles: Array.Empty<string>(),
            setValues: new Dictionary<string, string>
            {
                ["env.RAW"] = "a=b,c",
                ["env.BACKSLASH"] = "back\\slash",
            },
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Contains("env.RAW=a\\=b\\,c") && a.Contains("env.BACKSLASH=back\\\\slash")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpgradeInstallAsync_NonZeroExit_ThrowsHelmException()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(2, "", "Error: chart not found"));

        var ex = await Assert.That(async () => await _runner.UpgradeInstallAsync("r", "c", "1", "n",
                Array.Empty<string>(), new Dictionary<string, string>(), CancellationToken.None))
            .Throws<HelmException>();

        await Assert.That(ex!.ExitCode).IsEqualTo(2);
        await Assert.That(ex!.StdErr).Contains("chart not found");
    }

    [Test]
    public async Task UpgradeInstallDryRunAsync_AddsDryRunServerAndOmitsAtomic()
    {
        await _runner.UpgradeInstallDryRunAsync("acme-app", "acme/voest-app", "1.2.3", "octo",
            valuesFiles: new[] { "/tmp/values-a.yaml" },
            setValues: new Dictionary<string, string>(),
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "upgrade", "--install", "acme-app", "acme/voest-app",
                    "--version", "1.2.3",
                    "--namespace", "octo",
                    "--dry-run=server",
                    "-f", "/tmp/values-a.yaml",
                })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpgradeInstallDryRunAsync_NonZeroExit_ThrowsHelmExceptionTaggedAsDryRun()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(1, "", "Error: admission webhook denied"));

        var ex = await Assert.That(async () => await _runner.UpgradeInstallDryRunAsync("r", "c", "1", "n",
                Array.Empty<string>(), new Dictionary<string, string>(), CancellationToken.None))
            .Throws<HelmException>();

        await Assert.That(ex!.Operation).Contains("--dry-run=server");
        await Assert.That(ex!.StdErr).Contains("admission webhook denied");
    }

    [Test]
    public async Task UninstallAsync_BuildsExpectedArgsAndPassesIgnoreNotFound()
    {
        await _runner.UninstallAsync("acme-app", "octo", CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "uninstall", "acme-app",
                    "--namespace", "octo",
                    "--ignore-not-found",
                })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UninstallAsync_NonZeroExit_ThrowsHelmException()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(1, "", "Error: release pinned"));

        await Assert.That(async () => await _runner.UninstallAsync("r", "n", CancellationToken.None))
            .Throws<HelmException>();
    }

    [Test]
    public async Task GetLatestReleaseRevisionAsync_BuildsHistoryArgs_AndParsesNewestEntry()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0,
                """[{"revision":10,"status":"superseded"},{"revision":11,"status":"pending-upgrade"}]""",
                string.Empty));

        var latest = await _runner.GetLatestReleaseRevisionAsync("acme-app", "octo", CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "history", "acme-app", "--namespace", "octo", "-o", "json", "--max", "1",
                })),
            Arg.Any<CancellationToken>());
        await Assert.That(latest).IsNotNull();
        await Assert.That(latest!.Revision).IsEqualTo(11);
        await Assert.That(latest.IsPending).IsTrue();
    }

    [Test]
    public async Task GetLatestReleaseRevisionAsync_ReleaseNotFound_ReturnsNull()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(1, string.Empty, "Error: release: not found"));

        var latest = await _runner.GetLatestReleaseRevisionAsync("nope", "octo", CancellationToken.None);

        await Assert.That(latest).IsNull();
    }

    [Test]
    public async Task GetLatestReleaseRevisionAsync_UnparsableOutput_ReturnsNull()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0, "WARNING: not json", string.Empty));

        var latest = await _runner.GetLatestReleaseRevisionAsync("acme-app", "octo", CancellationToken.None);

        await Assert.That(latest).IsNull();
    }

    [Test]
    public async Task GetInstalledChartVersionAsync_BuildsListArgs_AndSplitsTheVersionOffTheChart()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0,
                """[{"name":"acme-app","status":"deployed","chart":"octo-mesh-app-1.0.71"}]""",
                string.Empty));

        var version = await _runner.GetInstalledChartVersionAsync("acme-app", "octo-mesh-app", "octo",
            CancellationToken.None);

        await _invoker.Received(1).InvokeAsync(
            Arg.Is<IReadOnlyList<string>>(a =>
                a.SequenceEqual(new[]
                {
                    "list", "--namespace", "octo", "--filter", "^acme-app$", "-o", "json",
                })),
            Arg.Any<CancellationToken>());
        // The chart name itself contains dashes, so the split only works against the known name.
        await Assert.That(version).IsEqualTo("1.0.71");
    }

    [Test]
    public async Task GetInstalledChartVersionAsync_NothingInstalled_ReturnsNull()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0, "[]", string.Empty));

        var version = await _runner.GetInstalledChartVersionAsync("nope", "octo-mesh-app", "octo",
            CancellationToken.None);

        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task GetInstalledChartVersionAsync_ForeignChart_ReturnsNullRatherThanGuessing()
    {
        // A release whose chart was swapped underneath us: deriving a version from an unrelated
        // chart name would pin the deploy to something that does not exist in this repository.
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0,
                """[{"name":"acme-app","status":"deployed","chart":"other-chart-9.9.9"}]""",
                string.Empty));

        var version = await _runner.GetInstalledChartVersionAsync("acme-app", "octo-mesh-app", "octo",
            CancellationToken.None);

        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task GetInstalledChartVersionAsync_HelmFails_ReturnsNull()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(1, string.Empty, "Error: Kubernetes cluster unreachable"));

        var version = await _runner.GetInstalledChartVersionAsync("acme-app", "octo-mesh-app", "octo",
            CancellationToken.None);

        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task GetInstalledChartVersionAsync_UnparsableOutput_ReturnsNull()
    {
        _invoker.InvokeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HelmProcessResult(0, "WARNING: not json", string.Empty));

        var version = await _runner.GetInstalledChartVersionAsync("acme-app", "octo-mesh-app", "octo",
            CancellationToken.None);

        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task HelmReleaseRevision_IsPending_MatchesOnlyPendingStates()
    {
        await Assert.That(new HelmReleaseRevision(1, "pending-install").IsPending).IsTrue();
        await Assert.That(new HelmReleaseRevision(2, "pending-upgrade").IsPending).IsTrue();
        await Assert.That(new HelmReleaseRevision(3, "pending-rollback").IsPending).IsTrue();
        await Assert.That(new HelmReleaseRevision(4, "deployed").IsPending).IsFalse();
        await Assert.That(new HelmReleaseRevision(5, "failed").IsPending).IsFalse();
    }
}
