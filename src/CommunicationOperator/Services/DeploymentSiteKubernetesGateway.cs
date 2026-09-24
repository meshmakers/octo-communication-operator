using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class DeploymentSiteKubernetesGateway : IDeploymentSiteKubernetesGateway
{
    private const string CrdGroup = "octo-mesh.meshmakers.io";
    private const string CrdVersion = "v1";
    private const string CrdPlural = "deploymentsites";

    private readonly IKubernetes _kubernetesClient;

    public DeploymentSiteKubernetesGateway(IKubernetes kubernetesClient)
    {
        _kubernetesClient = kubernetesClient;
    }

    public async Task<bool> DeploymentSiteExistsAsync(string @namespace, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _kubernetesClient.CustomObjects.GetNamespacedCustomObjectAsync(
                CrdGroup, CrdVersion, @namespace, CrdPlural, name, cancellationToken);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task CreateDeploymentSiteAsync(string @namespace, object resource, CancellationToken cancellationToken = default) =>
        _kubernetesClient.CustomObjects.CreateNamespacedCustomObjectAsync(
            resource, CrdGroup, CrdVersion, @namespace, CrdPlural, cancellationToken: cancellationToken);

    public Task DeleteDeploymentSiteAsync(string @namespace, string name, CancellationToken cancellationToken = default) =>
        _kubernetesClient.CustomObjects.DeleteNamespacedCustomObjectAsync(
            CrdGroup, CrdVersion, @namespace, CrdPlural, name, cancellationToken: cancellationToken);

    public async Task<bool> SecretExistsAsync(string @namespace, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _kubernetesClient.CoreV1.ReadNamespacedSecretAsync(name, @namespace, cancellationToken: cancellationToken);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task CreateSecretAsync(string @namespace, V1Secret secret, CancellationToken cancellationToken = default) =>
        _kubernetesClient.CoreV1.CreateNamespacedSecretAsync(secret, @namespace, cancellationToken: cancellationToken);

    public Task DeleteSecretAsync(string @namespace, string name, CancellationToken cancellationToken = default) =>
        _kubernetesClient.CoreV1.DeleteNamespacedSecretAsync(name, @namespace, cancellationToken: cancellationToken);

    /// <summary>
    ///     Field manager this operator writes under when it patches an object outside helm (AB#5325).
    /// </summary>
    /// <remarks>
    ///     Naming it is not cosmetic: an unnamed write is recorded as the manager <c>unknown</c>, which
    ///     is both unattributable and indistinguishable from a person's <c>kubectl patch</c>.
    /// </remarks>
    public const string FieldManagerName = "octo-communication-operator";

    public async Task<int> ScaleDeploymentsByInstanceAsync(string @namespace, string instance, int replicas,
        CancellationToken cancellationToken = default)
    {
        var deployments = await _kubernetesClient.AppsV1.ListNamespacedDeploymentAsync(@namespace,
            labelSelector: $"app.kubernetes.io/instance={instance}", cancellationToken: cancellationToken);

        var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{replicas}}}}}", V1Patch.PatchType.MergePatch);
        var patched = 0;
        foreach (var deployment in deployments.Items)
        {
            // 🔴 AB#5325: name the field manager. Without one the apiserver records this write under
            // the manager "unknown", and Helm 4's server-side apply then reports a conflict with
            // "unknown" on .spec.replicas on the NEXT deploy of the same workload — measured on
            // 2026-09-23, where a pool scale-up at 08:58 made the following deploy fail. An
            // anonymous manager also makes the conflict unattributable: the operator's own scale
            // path is indistinguishable from a person's kubectl patch, and the diagnosis then
            // blames a human who never touched it. The conflict itself is AB#5350.
            await _kubernetesClient.AppsV1.PatchNamespacedDeploymentAsync(patch,
                deployment.Metadata.Name, @namespace, fieldManager: FieldManagerName,
                cancellationToken: cancellationToken);
            patched++;
        }

        return patched;
    }

    public async Task<V1OwnerReference?> TryGetDeploymentSiteOwnerReferenceAsync(string @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        object raw;
        try
        {
            raw = await _kubernetesClient.CustomObjects.GetNamespacedCustomObjectAsync(
                CrdGroup, CrdVersion, @namespace, CrdPlural, name, cancellationToken);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // The custom-objects API is untyped; round-trip through the Kubernetes serializer rather
        // than poking at the raw JSON so the metadata shape stays owned by the client library.
        var entity = KubernetesJson.Deserialize<V1DeploymentSiteEntity>(KubernetesJson.Serialize(raw));
        var uid = entity?.Metadata?.Uid;
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        return new V1OwnerReference
        {
            ApiVersion = $"{CrdGroup}/{CrdVersion}",
            Kind = "DeploymentSite",
            Name = name,
            Uid = uid,
            // Not a controller reference: the deployment site's resources are created by helm, and claiming
            // controller ownership would make this operator the single controlling owner of
            // objects another component manages.
            Controller = false,
            // Blocking owner deletion needs `update` on the owner's finalizers subresource and
            // turns a tenant delete into a wait on every dependent. Garbage collection is a
            // safety net here, not a transaction.
            BlockOwnerDeletion = false,
        };
    }

    public async Task<int> SetDeploymentOwnerReferenceByInstanceAsync(string @namespace, string instance,
        V1OwnerReference ownerReference, CancellationToken cancellationToken = default)
    {
        var deployments = await _kubernetesClient.AppsV1.ListNamespacedDeploymentAsync(@namespace,
            labelSelector: $"app.kubernetes.io/instance={instance}", cancellationToken: cancellationToken);

        var body = KubernetesJson.Serialize(new V1Deployment
        {
            Metadata = new V1ObjectMeta { OwnerReferences = [ownerReference] },
        });
        var patch = new V1Patch(body, V1Patch.PatchType.MergePatch);

        var patched = 0;
        foreach (var deployment in deployments.Items)
        {
            await _kubernetesClient.AppsV1.PatchNamespacedDeploymentAsync(patch,
                deployment.Metadata.Name, @namespace, cancellationToken: cancellationToken);
            patched++;
        }

        return patched;
    }

    public async Task<DateTime?> GetSecretCreationTimestampAsync(string @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _kubernetesClient.CoreV1.ReadNamespacedSecretAsync(name, @namespace,
                cancellationToken: cancellationToken);
            return secret.Metadata?.CreationTimestamp;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
