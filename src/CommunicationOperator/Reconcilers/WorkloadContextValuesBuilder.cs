using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Builds the cluster-context YAML the operator passes to <c>helm upgrade</c>
/// as the <i>first</i> <c>-f</c> file. Carries non-secret values the operator
/// already knows about either the cluster (Mongo / RabbitMQ / CrateDB hosts,
/// reporting service URI, instance prefix, ingress defaults) or the workload
/// itself (tenant id, runtime entity id) so the per-workload
/// <c>ValuesYaml</c> does not have to repeat them.
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
    /// Returns the assembled context YAML, or <c>null</c> when neither the
    /// operator options nor the workload identity yield any value (e.g. an
    /// edge operator with empty options and a missing workload — which should
    /// not happen in practice).
    /// </summary>
    /// <param name="options">Operator-side cluster context.</param>
    /// <param name="workload">
    /// Workload being deployed. Supplies <c>tenantId</c> / <c>adapterRtId</c>
    /// at the top level so the chart can reference them without duplicating
    /// the values on the CK entity. Pass <c>null</c> only for unit tests that
    /// want to exercise the options layer in isolation.
    /// </param>
    public static string? Build(OperatorOptions options, WorkloadDeployedDto? workload = null)
    {
        var root = new Dictionary<string, object>();

        if (workload != null)
        {
            if (!string.IsNullOrWhiteSpace(workload.TenantId))
            {
                root["tenantId"] = workload.TenantId;
            }
            if (!string.IsNullOrWhiteSpace(workload.WorkloadRtId))
            {
                // Chart-side path stays "adapterRtId" — historical naming.
                // The value is the workload's runtime id regardless of
                // whether it is an Adapter or an Application; Application
                // charts that need it can read the same key.
                root["adapterRtId"] = workload.WorkloadRtId;
            }
        }

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

        if (!string.IsNullOrWhiteSpace(options.AuthUri))
        {
            root["authUri"] = options.AuthUri!;
        }

        if (!string.IsNullOrWhiteSpace(options.ImageRegistry))
        {
            // Adapter / application charts read `.Values.image.privateRegistry`
            // and prepend it to the image reference. The leaf path is nested
            // under `image` to match the chart's existing values shape.
            root["image"] = new Dictionary<string, object>
            {
                ["privateRegistry"] = options.ImageRegistry!,
            };
        }

        var deps = BuildClusterDependencies(options.ClusterDependencies);
        if (deps.Count > 0)
        {
            root["clusterDependencies"] = deps;
        }

        var ingress = BuildIngress(options.Ingress, workload);
        if (ingress.Count > 0)
        {
            root["ingress"] = ingress;
        }
        // publicUri is a top-level chart key, not nested under `ingress.*`
        // (see octo-mesh-adapter/templates/ingress.yaml host rule). Only
        // emitted when the workload has opted in and supplied a Hostname.
        if (workload is { IngressEnabled: true } && !string.IsNullOrWhiteSpace(workload.Hostname))
        {
            root["publicUri"] = $"https://{workload.Hostname}";
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

    private static Dictionary<string, object> BuildIngress(IngressDefaultsOptions ingress, WorkloadDeployedDto? workload)
    {
        var map = new Dictionary<string, object>();

        // Per-workload opt-in: only emit `ingress.enabled=true` when the workload
        // explicitly asked for it AND carries a hostname. The controller-side
        // validation already rejects IngressEnabled+empty Hostname at Deploy time;
        // the defensive Hostname check here keeps the operator path safe in case
        // an older controller version sends an inconsistent DTO. When false we
        // omit the key entirely — the chart's own default (ingress.enabled=false
        // in values.yaml) then wins, leaving the workload cluster-internal.
        if (workload is { IngressEnabled: true } && !string.IsNullOrWhiteSpace(workload.Hostname))
        {
            map["enabled"] = true;
        }

        // Cluster-wide defaults stack on top and apply to every workload regardless
        // of per-workload opt-in. Charts that don't render an Ingress (enabled
        // stays false) simply ignore these keys.
        if (!string.IsNullOrWhiteSpace(ingress.ClassName)) map["className"] = ingress.ClassName!;
        if (ingress.Tls.HasValue) map["tls"] = ingress.Tls.Value;

        var annotations = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(ingress.ClusterIssuer))
        {
            annotations["cert-manager.io/cluster-issuer"] = ingress.ClusterIssuer!;
        }
        foreach (var annotation in ingress.Annotations)
        {
            if (string.IsNullOrWhiteSpace(annotation.Name) || string.IsNullOrWhiteSpace(annotation.Value))
            {
                continue;
            }
            annotations[annotation.Name!] = annotation.Value!;
        }
        if (annotations.Count > 0)
        {
            map["annotations"] = annotations;
        }
        return map;
    }
}
