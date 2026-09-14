using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class CommunicationPoolKubernetesGateway : ICommunicationPoolKubernetesGateway
{
    private const string CrdGroup = "octo-mesh.meshmakers.io";
    private const string CrdVersion = "v1alpha1";
    private const string CrdPlural = "communicationpools";

    private readonly IKubernetes _kubernetesClient;

    public CommunicationPoolKubernetesGateway(IKubernetes kubernetesClient)
    {
        _kubernetesClient = kubernetesClient;
    }

    public async Task<bool> CommunicationPoolExistsAsync(string @namespace, string name, CancellationToken cancellationToken = default)
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

    public Task CreateCommunicationPoolAsync(string @namespace, object resource, CancellationToken cancellationToken = default) =>
        _kubernetesClient.CustomObjects.CreateNamespacedCustomObjectAsync(
            resource, CrdGroup, CrdVersion, @namespace, CrdPlural, cancellationToken: cancellationToken);

    public Task DeleteCommunicationPoolAsync(string @namespace, string name, CancellationToken cancellationToken = default) =>
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

    public async Task<int> ScaleDeploymentsByInstanceAsync(string @namespace, string instance, int replicas,
        CancellationToken cancellationToken = default)
    {
        var deployments = await _kubernetesClient.AppsV1.ListNamespacedDeploymentAsync(@namespace,
            labelSelector: $"app.kubernetes.io/instance={instance}", cancellationToken: cancellationToken);

        var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{replicas}}}}}", V1Patch.PatchType.MergePatch);
        var patched = 0;
        foreach (var deployment in deployments.Items)
        {
            await _kubernetesClient.AppsV1.PatchNamespacedDeploymentAsync(patch,
                deployment.Metadata.Name, @namespace, cancellationToken: cancellationToken);
            patched++;
        }

        return patched;
    }

    public async Task<V1OwnerReference?> TryGetCommunicationPoolOwnerReferenceAsync(string @namespace, string name,
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
        var entity = KubernetesJson.Deserialize<V1CommunicationPoolEntity>(KubernetesJson.Serialize(raw));
        var uid = entity?.Metadata?.Uid;
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        return new V1OwnerReference
        {
            ApiVersion = $"{CrdGroup}/{CrdVersion}",
            Kind = "CommunicationPool",
            Name = name,
            Uid = uid,
            // Not a controller reference: the pool's resources are created by helm, and claiming
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
