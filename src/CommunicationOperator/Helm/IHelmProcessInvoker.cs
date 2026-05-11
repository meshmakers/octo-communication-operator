namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
/// Low-level wrapper around the <c>helm</c> CLI binary. Exposes process
/// invocation as a pure async operation so the high-level <see cref="IHelmRunner"/>
/// is unit-testable without a real helm binary on the host.
/// </summary>
public interface IHelmProcessInvoker
{
    /// <summary>
    /// Invokes <c>helm</c> with the given arguments. Captures stdout and
    /// stderr. Never throws on a non-zero exit code — that's the caller's job.
    /// </summary>
    Task<HelmProcessResult> InvokeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a single helm CLI invocation. Exit code 0 means success;
/// stderr typically contains the actual error message on failure (helm
/// writes diagnostics there).
/// </summary>
public sealed record HelmProcessResult(int ExitCode, string StdOut, string StdErr);
