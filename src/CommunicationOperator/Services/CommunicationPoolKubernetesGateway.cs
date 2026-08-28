using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

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
