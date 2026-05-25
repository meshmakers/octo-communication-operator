using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Builds the overrides YAML the operator passes to <c>helm upgrade</c> as
/// an additional <c>-f</c> file (on top of the workload's base <c>ValuesYaml</c>).
///
/// Each <see cref="ValueOverrideDto"/> contributes one entry at its
/// dotted <c>Path</c>. Non-secret entries become the literal value. Secret
/// entries become a <c>valueFrom: secretKeyRef</c> envelope that points at
/// the operator-owned <c>{release}-octo-secrets</c> Secret — the chart at
/// the path must accept this shape (see
/// <c>project-helm-chart-secret-contract.md</c>).
/// </summary>
public static class WorkloadOverrideYamlBuilder
{
    /// <summary>
    /// Returns the assembled overrides YAML, or <c>null</c> when there are
    /// no overrides to apply.
    /// </summary>
    /// <param name="overrides">Overrides from the workload.</param>
    /// <param name="secretName">Name of the K8s Secret holding secret values.</param>
    public static string? Build(IReadOnlyList<ValueOverrideDto> overrides, string secretName)
    {
        if (overrides == null || overrides.Count == 0)
        {
            return null;
        }

        var root = new Dictionary<string, object>();
        foreach (var entry in overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            object value = entry.IsSecret
                ? BuildSecretRef(secretName, entry.Path)
                : entry.Value;

            SetNested(root, entry.Path.Split('.'), value);
        }

        if (root.Count == 0)
        {
            return null;
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithEventEmitter(next => new QuotedStringEventEmitter(next))
            .Build();
        return serializer.Serialize(root);
    }

    /// <summary>
    /// Secret keys are the same as the Helm path (dots preserved) so the
    /// chart-side template knows where to look:
    /// <c>{ valueFrom: { secretKeyRef: { name, key } } }</c>.
    /// </summary>
    private static object BuildSecretRef(string secretName, string path) => new Dictionary<string, object>
    {
        ["valueFrom"] = new Dictionary<string, object>
        {
            ["secretKeyRef"] = new Dictionary<string, object>
            {
                ["name"] = secretName,
                ["key"] = path,
            },
        },
    };

    private static void SetNested(Dictionary<string, object> root, IReadOnlyList<string> segments, object value)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var seg = segments[i];
            if (!current.TryGetValue(seg, out var next) || next is not Dictionary<string, object> nested)
            {
                nested = new Dictionary<string, object>();
                current[seg] = nested;
            }
            current = nested;
        }
        current[segments[^1]] = value;
    }
}
