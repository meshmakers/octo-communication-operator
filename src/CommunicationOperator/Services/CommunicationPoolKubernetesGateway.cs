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
}
