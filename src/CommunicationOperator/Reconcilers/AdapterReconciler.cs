using System.Collections.ObjectModel;
using k8s.Models;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Common;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

public class AdapterReconciler : IAdapterReconciler
{
    private const string ComponentDeploymentName = "communication-adapter";

    private readonly IKubernetesClient _kubernetesClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AdapterReconciler"/>
    /// </summary>
    /// <param name="kubernetesClient">Kubernetes client to use</param>
    /// <param name="logger">Logger to write log message to</param>
    public AdapterReconciler(IKubernetesClient kubernetesClient,
        ILogger<AdapterReconciler> logger)
    {
        _logger = logger;
        _kubernetesClient = kubernetesClient;
    }

    /// <summary>
    /// Reconciles the communication adapter for the pool resource.
    /// </summary>
    /// <param name="pool">Pool management object</param>
    /// <param name="poolAdapter">The pool communication adapter to reconcile</param>
    /// <param name="entity">Communication pool entity for reconcile</param>
    /// <exception cref="AdapterReconsilerException"></exception>
    public async Task ReconcileAsync(Pool pool, PoolCommunicationAdapterDto poolAdapter,
        V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("[{TenantId}] Reconciling communication adapter '{AdapterRtEntityId}'",
            pool.PoolDescriptor.TenantId,
            poolAdapter.AdapterRtEntityId);

        try
        {
            await ReconcileAdapterDeploymentAsync(pool.PoolDescriptor, poolAdapter);
            await ReconcileAdapterServiceAsync(pool.PoolDescriptor, poolAdapter);
            await pool.PoolHubClient.UpdateAdapterDeploymentStateAsync(pool.PoolDescriptor.PoolName,
                poolAdapter.AdapterRtEntityId, true);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while reconciling adapter '{AdapterRtEntityId}'",
                poolAdapter.AdapterRtEntityId);
            throw AdapterReconsilerException.AdapterReconcileFailed(poolAdapter.AdapterRtEntityId, e);
        }
    }

    public async Task DeleteAsync(K8Pool k8Pool)
    {
        _logger.LogInformation("[{TenantId}] Deleting pool '{PoolName}', namespace '{Namespace}'",
            k8Pool.TenantId, k8Pool.PoolName, k8Pool.Namespace);

        try
        {
            await DeleteAllAdapterDeploymentsAsync(k8Pool);
            await DeleteAllAdapterServicesAsync(k8Pool);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while deleting pool {PoolName}", k8Pool.PoolName);
            throw AdapterReconsilerException.PoolDeleteFailed(k8Pool.PoolName, e);
        }
    }

    public async Task DeleteAsync(Pool pool, PoolCommunicationAdapterDto poolAdapter)
    {
        _logger.LogInformation("[{TenantId}] Deleting adapter '{AdapterRtEntityId}', namespace '{Namespace}'",
            pool.PoolDescriptor.TenantId, poolAdapter.AdapterRtEntityId, pool.PoolDescriptor.Namespace);

        try
        {
            await DeleteAdapterDeploymentAsync(pool.PoolDescriptor, poolAdapter);
            await DeleteAdapterServiceAsync(pool.PoolDescriptor, poolAdapter);
            await pool.PoolHubClient.UpdateAdapterDeploymentStateAsync(pool.PoolDescriptor.PoolName,
                poolAdapter.AdapterRtEntityId, false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while deleting adapter '{AdapterRtEntityId}'",
                poolAdapter.AdapterRtEntityId);
            throw AdapterReconsilerException.AdapterDeleteFailed(poolAdapter.AdapterRtEntityId, e);
        }
    }

    private async Task DeleteAdapterServiceAsync(K8Pool k8Pool, PoolCommunicationAdapterDto poolAdapter)
    {
        _logger.LogInformation(
            "[{TenantId}] Deleting service for adapter '{AdapterRtEntityId}', namespace '{Namespace}'",
            k8Pool.TenantId, poolAdapter.AdapterRtEntityId, k8Pool.Namespace);

        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/adapter-ckId"] = poolAdapter.AdapterRtEntityId.CkTypeId.ToString().Replace("/", "-"),
            ["octo-mesh.meshmakers.io/adapter-rtId"] = poolAdapter.AdapterRtEntityId.RtId.ToString(),
            ["octo-mesh.meshmakers.io/pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.ListAsync<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        var existingService = existingServices.SingleOrDefault();
        if (existingService != null)
        {
            await DeleteAdapterService(k8Pool, existingService);
        }
    }

    private async Task DeleteAdapterDeploymentAsync(K8Pool k8Pool, PoolCommunicationAdapterDto poolAdapter)
    {
        _logger.LogInformation(
            "[{TenantId}] Deleting deployment for adapter '{AdapterRtEntityId}', namespace '{Namespace}'",
            k8Pool.TenantId, poolAdapter.AdapterRtEntityId, k8Pool.Namespace);

        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/adapter-ckId"] = poolAdapter.AdapterRtEntityId.CkTypeId.ToString().Replace("/", "-"),
            ["octo-mesh.meshmakers.io/adapter-rtId"] = poolAdapter.AdapterRtEntityId.RtId.ToString(),
            ["octo-mesh.meshmakers.io/pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingDeployments =
            await _kubernetesClient.ListAsync<V1Deployment>(k8Pool.Namespace,
                labelSelector: deploymentLabels.AsLabelSelector());

        var existingService = existingDeployments.SingleOrDefault();
        if (existingService != null)
        {
            await DeleteAdapterDeployment(k8Pool, existingService);
        }
    }

    private async Task DeleteAllAdapterDeploymentsAsync(K8Pool k8Pool)
    {
        _logger.LogInformation("[{TenantId}] Deleting all deployments for pool '{PoolName}', namespace '{Namespace}'",
            k8Pool.TenantId, k8Pool.PoolName, k8Pool.Namespace);
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };


        var existingDeployments =
            await _kubernetesClient.ListAsync<V1Deployment>(k8Pool.Namespace,
                labelSelector: deploymentLabels.AsLabelSelector());

        foreach (var existingDeployment in existingDeployments)
        {
            await DeleteAdapterDeployment(k8Pool, existingDeployment);
        }
    }

    private async Task DeleteAdapterDeployment(K8Pool k8Pool, V1Deployment existingDeployment)
    {
        _logger.LogInformation(
            "[{TenantId}] Deleting adapter deployment '{DeploymentName}' for pool '{PoolName}', namespace '{Namespace}'",
            k8Pool.TenantId, existingDeployment.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);

        _logger.DeletingDeployment(existingDeployment.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetesClient.DeleteAsync<V1Deployment>(existingDeployment.Metadata.Name, k8Pool.Namespace);
    }

    private async Task DeleteAllAdapterServicesAsync(K8Pool k8Pool)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.ListAsync<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        foreach (var existingService in existingServices)
        {
            await DeleteAdapterService(k8Pool, existingService);
        }
    }

    private async Task DeleteAdapterService(K8Pool k8Pool, V1Service existingService)
    {
        _logger.DeletingService(existingService.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetesClient.DeleteAsync<V1Service>(existingService.Metadata.Name, k8Pool.Namespace);
    }

    private async Task ReconcileAdapterDeploymentAsync(PoolDescriptor poolDescriptor,
        PoolCommunicationAdapterDto adapterDto)
    {
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/adapter-ckId"] = adapterDto.AdapterRtEntityId.CkTypeId.ToString().Replace("/", "-"),
            ["octo-mesh.meshmakers.io/adapter-rtId"] = adapterDto.AdapterRtEntityId.RtId.ToString(),
            ["octo-mesh.meshmakers.io/pool"] = poolDescriptor.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = poolDescriptor.TenantId
        };

        var existingDeployments =
            await _kubernetesClient.ListAsync<V1Deployment>(poolDescriptor.Namespace,
                labelSelector: deploymentLabels.AsLabelSelector());

        if (existingDeployments.Any())
        {
            await DeleteAdapterDeployment(poolDescriptor, existingDeployments.Single());
        }

        await CreateDeployment(poolDescriptor, adapterDto, deploymentLabels);
    }

    private async Task ReconcileAdapterServiceAsync(K8Pool k8Pool, PoolCommunicationAdapterDto adapterDto)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = ComponentDeploymentName,
            ["octo-mesh.meshmakers.io/adapter-ckId"] = adapterDto.AdapterRtEntityId.CkTypeId.ToString().Replace("/", "-"),
            ["octo-mesh.meshmakers.io/adapter-rtId"] = adapterDto.AdapterRtEntityId.RtId.ToString(),
            ["octo-mesh.meshmakers.io/pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.ListAsync<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        if (existingServices.Any())
        {
            await DeleteAdapterService(k8Pool, existingServices.Single());
        }

        await CreateService(k8Pool, adapterDto, serviceLabels);
    }

    private async Task CreateDeployment(PoolDescriptor poolDescriptor, PoolCommunicationAdapterDto adapterDto,
        Dictionary<string, string> deploymentLabels)
    {
        var deploymentName =
            $"{poolDescriptor.TenantId}-{adapterDto.AdapterRtEntityId.CkTypeId.Key.TypeId.ToLower()}-{adapterDto.AdapterRtEntityId.RtId.ToString()}";

        _logger.CreatingDeployment(deploymentName, poolDescriptor.PoolName, poolDescriptor.Namespace);

        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        _logger.LogInformation("Architecture: {Architecture}", architecture);
        string architectureString = "amd64";
        if (architecture == "Arm64")
        {
            architectureString = "arm64v8";
        }

        var deploymentImageName = adapterDto.ImageName + ":" + architectureString + "-" + adapterDto.Version;
        _logger.LogInformation("Image: {Image}", deploymentImageName);

        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = deploymentName,
                NamespaceProperty = poolDescriptor.Namespace,
                Labels = deploymentLabels
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector
                {
                    MatchLabels = deploymentLabels
                },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = deploymentLabels,
                        Name = deploymentName
                    },
                    Spec = new V1PodSpec
                    {
                        Containers = new Collection<V1Container>
                        {
                            new()
                            {
                                Name = deploymentName,
                                Image = deploymentImageName,
                                // Command = new Collection<string> { "prefect", "orion", "start" },
                                Env = CreateEnvironment(poolDescriptor, adapterDto),
                                Ports = new Collection<V1ContainerPort>
                                {
                                    new(containerPort: 4200, name: "http-orion")
                                },
                                Resources = new V1ResourceRequirements
                                {
                                    Requests = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new("200m"),
                                        ["memory"] = new("512Mi")
                                    },
                                    Limits = new Dictionary<string, ResourceQuantity>
                                    {
                                        ["cpu"] = new("500m"),
                                        ["memory"] = new("1Gi")
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        await _kubernetesClient.CreateAsync(deployment);
    }

    private Collection<V1EnvVar> CreateEnvironment(PoolDescriptor poolDescriptor,
        PoolCommunicationAdapterDto adapterDto)
    {
        var secretName = $"{poolDescriptor.TenantId}-{poolDescriptor.PoolName}-octo-mesh-connection";

        var collection = new Collection<V1EnvVar>();

        collection.Add(new()
        {
            Name = "OCTO_ADAPTER__TENANTID",
            Value = poolDescriptor.TenantId
        });
        collection.Add(new()
        {
            Name = "OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI",
            Value = poolDescriptor.ControllerUri
        });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__ADAPTERCKTYPEID",
                Value = adapterDto.AdapterRtEntityId.CkTypeId.ToString()
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__ADAPTERRTID",
                Value = adapterDto.AdapterRtEntityId.RtId.ToString()
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__BROKERHOST",
                Value = poolDescriptor.BrokerHost
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__BROKERVIRTUALHOST",
                Value = poolDescriptor.BrokerVirtualHost
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__BROKERPORT",
                Value = poolDescriptor.BrokerPort.ToString()
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__BROKERUSERNAME",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = secretName,
                        Key = "brokerusername"
                    }
                }
            });
        collection.Add(
            new()
            {
                Name = "OCTO_ADAPTER__BROKERPASSWORD",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = secretName,
                        Key = "brokerpassword"
                    }
                }
            });


        return collection;
    }

    private async Task CreateService(K8Pool k8Pool, PoolCommunicationAdapterDto adapterDto,
        Dictionary<string, string> serviceLabels)
    {
        var serviceName =
            $"{k8Pool.TenantId}-{adapterDto.AdapterRtEntityId.CkTypeId.Key.TypeId.ToLower()}-{adapterDto.AdapterRtEntityId.RtId}";

        _logger.CreatingService(serviceName, k8Pool.PoolName, k8Pool.Namespace);

        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName,
                NamespaceProperty = k8Pool.Namespace,
                Labels = serviceLabels,
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = serviceLabels,
                Ports = new Collection<V1ServicePort>
                {
                    new()
                    {
                        Name = "http-orion",
                        Port = 4200,
                        Protocol = "TCP",
                        TargetPort = 4200
                    }
                }
            }
        };

        await _kubernetesClient.CreateAsync(service);
    }
}