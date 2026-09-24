using System.Text;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Communication.Operator.Helm;

/// <summary>
///     Recognises a server-side-apply field-ownership conflict in a failed <c>helm</c> invocation and
///     turns it into an answer an operator can act on (AB#5325).
/// </summary>
/// <remarks>
///     <para>
///         Helm 4 applies server-side by default (<c>--server-side auto</c>), so a field another
///         manager owns is not merged — the apply is refused. The only way a foreign manager appears on
///         a workload this operator deploys is a human writing to it directly: <c>kubectl set image</c>,
///         <c>kubectl set env</c> or <c>kubectl patch</c>, all of which leave a manager called
///         <c>kubectl-set</c> or <c>kubectl-patch</c> behind.
///     </para>
///     <para>
///         🔴 <b>It is not a transient failure, and that is what makes the diagnosis worth building.</b>
///         The conflict is recorded in the object's <c>managedFields</c>, so it is there on the next
///         deploy and the one after that. Worse, helm's own rollback applies the same way and fails for
///         the same reason, which leaves the release in <c>failed</c> state: a retry makes the
///         situation worse rather than merely not better. Observed twice on 2026-09-23, on the
///         operator's own Deployment (patched with <c>kubectl set image</c>) and on an adapter pool
///         member (patched with <c>kubectl set env</c>).
///     </para>
///     <para>
///         Without this, the operator forwarded helm's stderr verbatim onto the workload's
///         <c>LastDeploymentError</c>. That text does contain the word "conflict" and the manager's
///         name, but it names neither the cause nor any way out, and it arrives mixed into a wall of
///         rollback noise.
///     </para>
/// </remarks>
public static partial class HelmFieldOwnershipConflict
{
    /// <summary>
    ///     Managers helm itself uses. A conflict with one of these is not a hand patch and must not be
    ///     reported as one — it would send an operator looking for a person who was never there.
    /// </summary>
    private static readonly string[] HelmOwnManagers = ["helm", "helm-controller"];

    [GeneratedRegex("""conflicts?\s+with\s+"(?<manager>[^"]+)"\s+using""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManagerPattern();

    /// <summary>Field paths helm lists, either inline after the manager or as "- .spec…" bullets.</summary>
    /// <remarks>
    ///     🔴 The backslash in the character class is load-bearing. Helm embeds the failing apply in a
    ///     quoted <c>error="…"</c> field, so the field paths arrive with their quotes ESCAPED —
    ///     <c>.env[name=\"OCTO_ADAPTER__BROKERHOST\"]</c>. Without the backslash every path was cut
    ///     off at <c>containers[name=</c>, which made all of them identical after de-duplication and
    ///     left the message naming no field at all. Found by testing against verbatim stderr rather
    ///     than against a hand-written sample, which had no escaping in it.
    /// </remarks>
    [GeneratedRegex(@"(?<path>\.(?:spec|metadata|data|stringData)[A-Za-z0-9_.\[\]{}""\\\-=:@/]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldPathPattern();

    /// <summary>
    ///     The foreign field managers named in <paramref name="helmStdErr" />, or an empty list when
    ///     this failure is not a field-ownership conflict.
    /// </summary>
    public static IReadOnlyList<string> ForeignManagers(string? helmStdErr)
    {
        if (string.IsNullOrWhiteSpace(helmStdErr))
        {
            return [];
        }

        return ManagerPattern().Matches(helmStdErr)
            .Select(match => match.Groups["manager"].Value)
            .Where(manager => !HelmOwnManagers.Contains(manager, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether this failure is a field-ownership conflict with a manager that is not helm.</summary>
    public static bool IsFieldOwnershipConflict(string? helmStdErr)
    {
        return ForeignManagers(helmStdErr).Count > 0;
    }

    /// <summary>
    ///     The operator-facing explanation: what happened, what owns the fields, and the two ways out.
    /// </summary>
    /// <remarks>
    ///     Both remedies are named because they are genuinely different decisions.
    ///     <b>Deleting the Deployment</b> discards the hand-written values and lets the chart own the
    ///     object again — correct when the patch was a stop-gap, which is what it usually is.
    ///     <b>Enabling <c>Helm.ForceConflicts</c></b> makes every later deploy take ownership silently,
    ///     which is a standing decision to overwrite whatever anyone sets by hand, so it is an
    ///     opt-in and not something this path does on its own.
    /// </remarks>
    public static string Explain(string release, string @namespace, string? helmStdErr)
    {
        var managers = ForeignManagers(helmStdErr);
        var fields = FieldPaths(helmStdErr);

        var message = new StringBuilder();
        message.Append($"Helm could not apply release '{release}' in namespace '{@namespace}': ");
        message.Append(managers.Count == 1
            ? $"the field manager '{managers[0]}' owns fields of the object"
            : $"the field managers {string.Join(", ", managers.Select(m => $"'{m}'"))} own fields of the object");
        message.Append(" and server-side apply refuses to overwrite them. ");
        message.Append(
            "That manager is not helm, so the object was written to directly — kubectl set image / set env / patch " +
            "leave exactly this behind. ");

        if (fields.Count > 0)
        {
            message.Append($"Contested field(s): {string.Join(", ", fields)}. ");
        }

        message.Append(
            "This does not clear by itself: the ownership is recorded in the object's managedFields, and helm's own " +
            "rollback fails the same way, which leaves the release in 'failed' state — retrying makes it worse, not " +
            "better. Either delete the Deployment so the chart owns it again (the hand-written values are lost, " +
            "which is usually the point), or set the operator option Helm.ForceConflicts=true to let every future " +
            "deploy take ownership — the second is a standing decision to overwrite anything set by hand.");

        return message.ToString();
    }

    /// <summary>The contested field paths, de-duplicated and capped so one message stays readable.</summary>
    private static IReadOnlyList<string> FieldPaths(string? helmStdErr)
    {
        if (string.IsNullOrWhiteSpace(helmStdErr))
        {
            return [];
        }

        return FieldPathPattern().Matches(helmStdErr)
            // The escaping belongs to helm's own quoting of the error, not to the field path.
            .Select(match => match.Groups["path"].Value.Replace("\\", string.Empty).TrimEnd('.', ',', ';'))
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
    }
}
