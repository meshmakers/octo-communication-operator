using System.Diagnostics;
using System.Text;

namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
/// Runs the <c>helm</c> binary on PATH and captures its output. Production
/// implementation — replaced by a substitute in unit tests.
/// </summary>
public sealed class HelmProcessInvoker(ILogger<HelmProcessInvoker> logger) : IHelmProcessInvoker
{
    private const string HelmExecutable = "helm";

    public async Task<HelmProcessResult> InvokeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HelmExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        logger.LogDebug("Invoking helm: {Arguments}",
            // Mask --password values in the log line for safety.
            MaskPasswords(arguments));

        using var process = new Process { StartInfo = startInfo };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        return new HelmProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static string MaskPasswords(IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            if (i > 0 && (arguments[i - 1] == "--password" || arguments[i - 1] == "--username"))
            {
                sb.Append("***");
            }
            else
            {
                sb.Append(arguments[i]);
            }
        }
        return sb.ToString();
    }
}
