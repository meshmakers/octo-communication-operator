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
}
