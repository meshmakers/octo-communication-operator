namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
/// Builds <c>helm</c> CLI argument lists and delegates execution to an
/// <see cref="IHelmProcessInvoker"/>. Translates non-zero exit codes into
/// <see cref="HelmException"/> (with one well-defined exception: a
/// <c>helm uninstall</c> against a non-existent release is success).
/// </summary>
public sealed class HelmRunner(IHelmProcessInvoker invoker, ILogger<HelmRunner> logger) : IHelmRunner
{
    public async Task EnsureRepoAsync(string alias, string url, string? username, string? password,
        CancellationToken cancellationToken)
    {
        var addArgs = new List<string> { "repo", "add", alias, url, "--force-update" };
        if (!string.IsNullOrEmpty(username))
        {
            addArgs.Add("--username");
            addArgs.Add(username);
        }
        if (!string.IsNullOrEmpty(password))
        {
            addArgs.Add("--password");
            addArgs.Add(password);
        }

        var addResult = await invoker.InvokeAsync(addArgs, cancellationToken);
        if (addResult.ExitCode != 0)
        {
            throw new HelmException($"repo add {alias}", addResult.ExitCode, addResult.StdOut, addResult.StdErr);
        }

        var updateArgs = new List<string> { "repo", "update", alias };
        var updateResult = await invoker.InvokeAsync(updateArgs, cancellationToken);
        if (updateResult.ExitCode != 0)
        {
            throw new HelmException($"repo update {alias}", updateResult.ExitCode, updateResult.StdOut,
                updateResult.StdErr);
        }

        logger.LogInformation("Helm repo '{Alias}' registered ({Url})", alias, url);
    }

    public async Task UpgradeInstallAsync(string release, string chart, string version, string @namespace,
        IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
        CancellationToken cancellationToken)
    {
        var args = BuildUpgradeArgs(release, chart, version, @namespace, valuesFiles, setValues, dryRunServer: false);

        var result = await invoker.InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HelmException($"upgrade --install {release}", result.ExitCode, result.StdOut, result.StdErr);
        }

        logger.LogInformation("Helm release '{Release}' (chart {Chart} {Version}) installed/upgraded in namespace '{Namespace}'",
            release, chart, version, @namespace);
    }

    public async Task UpgradeInstallDryRunAsync(string release, string chart, string version, string @namespace,
        IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
        CancellationToken cancellationToken)
    {
        var args = BuildUpgradeArgs(release, chart, version, @namespace, valuesFiles, setValues, dryRunServer: true);

        var result = await invoker.InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HelmException($"upgrade --install --dry-run=server {release}", result.ExitCode, result.StdOut, result.StdErr);
        }

        logger.LogDebug("Helm release '{Release}' pre-flight (dry-run=server) succeeded", release);
    }

    private static List<string> BuildUpgradeArgs(string release, string chart, string version, string @namespace,
        IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues, bool dryRunServer)
    {
        // Note: no `--create-namespace` — the pool namespace is owned by the
        // operator's namespace-scoped service account, which cannot create
        // cluster-scoped resources. The pool's namespace is guaranteed to
        // exist before any workload deploy: CommunicationPoolManager creates
        // it (or asserts it) when the CommunicationPool CR is created.
        var args = new List<string>
        {
            "upgrade", "--install", release, chart,
        };

        // Pass --version only when the workload entity carries a concrete chart version.
        // The System.Communication.MainLatest blueprint deliberately seeds an empty
        // ChartVersion on dev/test clusters so the first Deploy resolves to the newest
        // chart in the configured repo (the rolling dev channel); helm rejects an empty
        // value for --version, so we have to omit the flag entirely in that case. Once
        // the CD rollout pipeline writes a concrete version onto the workload, every
        // subsequent deploy goes back through the pinned-version path below.
        if (!string.IsNullOrWhiteSpace(version))
        {
            args.Add("--version");
            args.Add(version);
        }

        args.Add("--namespace");
        args.Add(@namespace);

        if (dryRunServer)
        {
            // --atomic is meaningless for a dry-run (nothing to roll back) and
            // forces helm to wait for resources that will never exist.
            args.Add("--dry-run=server");
        }
        else
        {
            args.Add("--atomic");
        }

        foreach (var file in valuesFiles)
        {
            args.Add("-f");
            args.Add(file);
        }
        foreach (var (path, value) in setValues)
        {
            args.Add("--set");
            args.Add($"{path}={EscapeSetValue(value)}");
        }

        return args;
    }

    public async Task UninstallAsync(string release, string @namespace, CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "uninstall", release,
            "--namespace", @namespace,
            "--ignore-not-found",
        };

        var result = await invoker.InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HelmException($"uninstall {release}", result.ExitCode, result.StdOut, result.StdErr);
        }

        logger.LogInformation("Helm release '{Release}' uninstalled from namespace '{Namespace}'",
            release, @namespace);
    }

    public async Task<HelmReleaseRevision?> GetLatestReleaseRevisionAsync(string release, string @namespace,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "history", release,
            "--namespace", @namespace,
            "-o", "json",
            "--max", "1",
        };

        var result = await invoker.InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            // "release: not found" is the common case (fresh install) — not an error for callers.
            logger.LogDebug("helm history for release '{Release}' returned exit code {ExitCode}: {StdErr}",
                release, result.ExitCode, result.StdErr);
            return null;
        }

        return ParseLatestRevision(result.StdOut, release);
    }

    /// <summary>
    /// Parses the <c>helm history -o json</c> output (a JSON array of revision entries) and
    /// returns the newest one. Internal so the parsing contract is unit-testable without a
    /// helm binary.
    /// </summary>
    internal HelmReleaseRevision? ParseLatestRevision(string historyJson, string release)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(historyJson);
            HelmReleaseRevision? latest = null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var revision = entry.GetProperty("revision").GetInt32();
                var status = entry.GetProperty("status").GetString() ?? string.Empty;
                if (latest == null || revision > latest.Revision)
                {
                    latest = new HelmReleaseRevision(revision, status);
                }
            }

            return latest;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not parse helm history output for release '{Release}'", release);
            return null;
        }
    }

    /// <summary>
    /// Escapes commas and equals signs in <c>--set</c> values. Helm uses these
    /// as separators, so any value containing them needs each character
    /// backslash-escaped. Backslash itself is doubled.
    /// </summary>
    private static string EscapeSetValue(string value) =>
        value.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");
}
