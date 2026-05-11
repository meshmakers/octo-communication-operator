namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
/// Thrown when a <c>helm</c> CLI invocation fails. Carries the full stdout
/// and stderr so the caller can decide whether to log, surface or swallow.
/// </summary>
public sealed class HelmException : Exception
{
    public HelmException(string operation, int exitCode, string stdOut, string stdErr)
        : base($"helm {operation} failed with exit code {exitCode}. stderr: {stdErr.TrimEnd()}")
    {
        Operation = operation;
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public string Operation { get; }
    public int ExitCode { get; }
    public string StdOut { get; }
    public string StdErr { get; }
}
