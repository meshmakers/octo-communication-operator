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
        // Note: no `--create-namespace` — the pool namespace is owned by the
        // operator's namespace-scoped service account, which cannot create
        // cluster-scoped resources. The pool's namespace is guaranteed to
        // exist before any workload deploy: CommunicationPoolManager creates
        // it (or asserts it) when the CommunicationPool CR is created.
        var args = new List<string>
        {
            "upgrade", "--install", release, chart,
            "--version", version,
            "--namespace", @namespace,
            "--atomic",
        };

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

        var result = await invoker.InvokeAsync(args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new HelmException($"upgrade --install {release}", result.ExitCode, result.StdOut, result.StdErr);
        }

        logger.LogInformation("Helm release '{Release}' (chart {Chart} {Version}) installed/upgraded in namespace '{Namespace}'",
            release, chart, version, @namespace);
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

    /// <summary>
    /// Escapes commas and equals signs in <c>--set</c> values. Helm uses these
    /// as separators, so any value containing them needs each character
    /// backslash-escaped. Backslash itself is doubled.
    /// </summary>
    private static string EscapeSetValue(string value) =>
        value.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");
}
