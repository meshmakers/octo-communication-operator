using Meshmakers.Octo.Communication.Operator.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Builds the cluster-context YAML the operator passes to <c>helm upgrade</c>
/// as the <i>first</i> <c>-f</c> file. Carries non-secret values that the
/// operator already knows about its cluster (Mongo / RabbitMQ / CrateDB hosts,
/// reporting service URI, instance prefix, ingress defaults) so that
/// per-workload <c>ValuesYaml</c> does not have to repeat them.
///
/// Helm value precedence is preserved by file order: this layer is the
/// lowest, the workload's own <c>ValuesYaml</c> overrides it, and structured
/// per-value overrides override both.
///
/// Secret-flagged values are deliberately <b>not</b> handled here — those go
/// through <see cref="WorkloadOverrideYamlBuilder"/> via the operator-owned
/// <c>{release}-octo-secrets</c> Secret.
/// </summary>
public static class WorkloadContextValuesBuilder
{
    /// <summary>
    /// Returns the assembled context YAML, or <c>null</c> when no option is
    /// set (e.g. an edge operator that lets the workload supply everything).
    /// </summary>
    public static string? Build(OperatorOptions options)
    {
        var root = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(options.InstancePrefix))
        {
            root["instancePrefix"] = options.InstancePrefix!;
        }

        if (!string.IsNullOrWhiteSpace(options.CommunicationControllerUri))
        {
            root["communicationControllerServiceUri"] = options.CommunicationControllerUri;
        }

        if (!string.IsNullOrWhiteSpace(options.ReportingServiceUri))
        {
            root["reportingServiceUri"] = options.ReportingServiceUri!;
        }

        var deps = BuildClusterDependencies(options.ClusterDependencies);
        if (deps.Count > 0)
        {
            root["clusterDependencies"] = deps;
        }

        var ingress = BuildIngress(options.Ingress);
        if (ingress.Count > 0)
        {
            root["ingress"] = ingress;
        }

        if (root.Count == 0)
        {
            return null;
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return serializer.Serialize(root);
    }

    private static Dictionary<string, object> BuildClusterDependencies(ClusterDependenciesOptions deps)
    {
        var map = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(deps.MongodbHost)) map["mongodbHost"] = deps.MongodbHost!;
        if (!string.IsNullOrWhiteSpace(deps.MongodbReplicaSet)) map["mongodbReplicaSet"] = deps.MongodbReplicaSet!;
        if (!string.IsNullOrWhiteSpace(deps.RabbitMqHost)) map["rabbitMqHost"] = deps.RabbitMqHost!;
        if (!string.IsNullOrWhiteSpace(deps.RabbitMqUser)) map["rabbitMqUser"] = deps.RabbitMqUser!;
        if (!string.IsNullOrWhiteSpace(deps.StreamDataHost)) map["streamDataHost"] = deps.StreamDataHost!;
        if (!string.IsNullOrWhiteSpace(deps.StreamDataUser)) map["streamDataUser"] = deps.StreamDataUser!;
        return map;
    }

    private static Dictionary<string, object> BuildIngress(IngressDefaultsOptions ingress)
    {
        var map = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(ingress.ClassName)) map["className"] = ingress.ClassName!;
        if (ingress.Tls.HasValue) map["tls"] = ingress.Tls.Value;
        if (!string.IsNullOrWhiteSpace(ingress.ClusterIssuer))
        {
            map["annotations"] = new Dictionary<string, object>
            {
                ["cert-manager.io/cluster-issuer"] = ingress.ClusterIssuer!,
            };
        }
        return map;
    }
}
