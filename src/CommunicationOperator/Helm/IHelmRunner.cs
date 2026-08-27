namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
/// High-level wrapper around the <c>helm</c> CLI. Knows how to register chart
/// repositories, install/upgrade releases and uninstall them. Errors surface
/// as <see cref="HelmException"/> — never silent failures.
/// </summary>
public interface IHelmRunner
{
    /// <summary>
    /// Idempotently registers a Helm chart repository and refreshes its index.
    /// Equivalent to:
    /// <code>
    /// helm repo add {alias} {url} [--username --password]   # or --force-update
    /// helm repo update {alias}
    /// </code>
    /// </summary>
    Task EnsureRepoAsync(string alias, string url, string? username, string? password,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>helm upgrade --install</c> for the given release.
    /// </summary>
    /// <param name="release">Helm release name (typically <c>{tenant}-{workload}</c>).</param>
    /// <param name="chart">Chart reference, e.g. <c>{alias}/{chartName}</c>.</param>
    /// <param name="version">Chart version.</param>
    /// <param name="namespace">Kubernetes namespace.</param>
    /// <param name="valuesFiles">Files passed via <c>-f</c>. Later files override earlier ones.</param>
    /// <param name="setValues">Inline overrides passed via <c>--set</c>.</param>
    Task UpgradeInstallAsync(string release, string chart, string version, string @namespace,
        IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>helm upgrade --install --dry-run=server</c> for the given
    /// release. Same arguments as <see cref="UpgradeInstallAsync"/> minus
    /// <c>--atomic</c> (nothing to roll back) plus <c>--dry-run=server</c>,
    /// which submits the manifests to the API server with <c>dryRun=All</c>
    /// — so admission webhooks, OpenAPI schema validation and RBAC checks
    /// all run, but no resources are created. Catches misconfigurations
    /// (missing required values, RBAC, Gatekeeper policies, invalid
    /// annotations) in &lt;2s instead of waiting 5min for the atomic
    /// timeout. Does NOT catch ImagePull / CrashLoop / probe failures
    /// because no pods are created — those are covered by the
    /// post-failure diagnostic collector path in
    /// <see cref="Diagnostics.IWorkloadDiagnosticsCollector"/>.
    /// </summary>
    Task UpgradeInstallDryRunAsync(string release, string chart, string version, string @namespace,
        IReadOnlyList<string> valuesFiles, IReadOnlyDictionary<string, string> setValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>helm uninstall</c>. A release that does not exist is treated
    /// as success.
    /// </summary>
    Task UninstallAsync(string release, string @namespace, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the newest revision of the release via <c>helm history {release} -o json --max 1</c>,
    /// or <c>null</c> when the release does not exist (or the history cannot be read). Used by the
    /// stale-lock recovery (AB#4894): a helm process killed mid-upgrade (operator pod replacement)
    /// leaves the newest revision in a <c>pending-*</c> status that blocks every later
    /// install/upgrade/rollback with "another operation is in progress".
    /// </summary>
    Task<HelmReleaseRevision?> GetLatestReleaseRevisionAsync(string release, string @namespace,
        CancellationToken cancellationToken);
}

/// <summary>
/// One entry of <c>helm history</c>: the revision number and its status
/// (e.g. <c>deployed</c>, <c>superseded</c>, <c>pending-install</c>, <c>pending-upgrade</c>).
/// </summary>
public sealed record HelmReleaseRevision(int Revision, string Status)
{
    /// <summary>Whether the revision is stuck in one of helm's <c>pending-*</c> states.</summary>
    public bool IsPending => Status.StartsWith("pending-", StringComparison.OrdinalIgnoreCase);
}
