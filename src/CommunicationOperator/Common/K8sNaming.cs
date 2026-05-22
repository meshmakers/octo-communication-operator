using System.Text;

namespace Meshmakers.Octo.Communication.Operator.Common;

/// <summary>
/// Coerces arbitrary strings (tenant ids, pool names, workload names from
/// the CK entity store) into shapes the Kubernetes apiserver will accept.
///
/// Two flavours:
/// <list type="bullet">
///   <item><see cref="DnsName"/> — strict RFC 1123 subdomain segment used
///   for resource names and helm release names (lowercase, alphanumeric +
///   <c>'-'</c>).</item>
///   <item><see cref="LabelValue"/> — label-value alphabet (allows
///   <c>'_'</c> and <c>'.'</c>) for the
///   <c>octo-mesh.meshmakers.io/{tenant,pool,workload}</c> identity
///   labels.</item>
/// </list>
///
/// The CR/secret/release-name path used to call <c>ToLowerInvariant()</c>
/// directly which silently produced apiserver-rejected names whenever the
/// pool was named, e.g., <c>"Communication Pool"</c>. Both helpers are
/// pure and side-effect-free; suitable for use from the workload
/// reconciler, the communication-pool manager, and any future component
/// that has to derive k8s identifiers from CK entity attributes.
/// </summary>
internal static class K8sNaming
{
    /// <summary>
    /// Default DNS-name length cap. Matches Helm's release-name limit; the
    /// stricter constraint here protects every dependant resource (Secret,
    /// CR, label) the apiserver will reject above its own limit.
    /// </summary>
    public const int DefaultDnsNameMaxLength = 53;

    /// <summary>
    /// k8s label-value length limit per the API spec.
    /// </summary>
    public const int LabelValueMaxLength = 63;

    /// <summary>
    /// Lowercases, collapses non-alphanumeric characters to <c>'-'</c>,
    /// trims leading/trailing dashes, and truncates to
    /// <paramref name="maxLength"/> (default 53). Throws when the input is
    /// blank or sanitises to an empty string — callers should refuse to
    /// build apiserver names from such inputs rather than substitute a
    /// placeholder and risk silent identity collisions.
    /// </summary>
    public static string DnsName(string value, int maxLength = DefaultDnsNameMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("k8s DNS name component must not be empty.", nameof(value));
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('-');
            }
        }

        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal))
        {
            s = s.Replace("--", "-", StringComparison.Ordinal);
        }
        s = s.Trim('-');

        if (s.Length == 0)
        {
            throw new ArgumentException(
                $"k8s DNS name component '{value}' sanitised to empty.", nameof(value));
        }

        if (s.Length > maxLength)
        {
            s = s[..maxLength].TrimEnd('-');
        }

        return s;
    }

    /// <summary>
    /// Joins <paramref name="parts"/> with <c>'-'</c> after individually
    /// sanitising each piece. Used for compound names like
    /// <c>{tenant}-{pool}</c> or <c>{tenant}-{workload}</c> where the
    /// individual segments come from user-controlled CK attributes.
    /// </summary>
    public static string DnsName(int maxLength, params string[] parts)
    {
        var joined = string.Join('-', parts);
        return DnsName(joined, maxLength);
    }

    /// <summary>
    /// Coerces an arbitrary string into a valid Kubernetes label value.
    /// Kubernetes requires labels to match
    /// <c>[A-Za-z0-9][-A-Za-z0-9_.]*[A-Za-z0-9]</c> with length ≤ 63 — CK
    /// entity names can contain spaces or other punctuation (e.g.
    /// <c>"Communication Pool"</c>, <c>"meshtest Adapter"</c>) which the
    /// apiserver rejects with a 422. Everything outside the allowed
    /// alphabet becomes a dash; leading and trailing punctuation are
    /// trimmed; empty results become <c>"unknown"</c> so the label is
    /// still set (omitting it would lose the identity in the labels API).
    /// </summary>
    public static string LabelValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        var trimmed = sb.ToString().Trim('-', '_', '.');
        if (trimmed.Length == 0)
        {
            return "unknown";
        }
        return trimmed.Length > LabelValueMaxLength
            ? trimmed[..LabelValueMaxLength].TrimEnd('-', '_', '.')
            : trimmed;
    }
}
