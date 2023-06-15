using System.Collections.ObjectModel;
using k8s.Models;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using PlugOperator.Common;
using PlugOperator.Entities;
using PlugOperator.Models;

namespace PlugOperator.Reconcilers;

public class PlugReconciler : IPlugReconciler
{
    private readonly IKubernetesClient _kubernetesClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PlugReconciler"/>
    /// </summary>
    /// <param name="kubernetesClient">Kubernetes client to use</param>
    /// <param name="logger">Logger to write log message to</param>
    public PlugReconciler(IKubernetesClient kubernetesClient, ILogger<PlugReconciler> logger)
    {
        _logger = logger;
        _kubernetesClient = kubernetesClient;
    }

    /// <summary>
    /// Reconciles the plugs for the plug pool resource.
    /// </summary>
    /// <param name="poolDescriptor">Meta data about the pool</param>
    /// <param name="plugPoolPlug">The pool plug to reconcile</param>
    /// <param name="entity">Plug pool entity for reconcile</param>
    public async Task ReconcileAsync(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity)
    {
        _logger.LogInformation("[{TenantId}] Reconciling plug '{PlugId}'", poolDescriptor.TenantId, plugPoolPlug.PlugRtId);
        
        try
        {
            await ReconcilePlugDeploymentAsync(poolDescriptor, plugPoolPlug);
            await ReconcilePlugServiceAsync(poolDescriptor, plugPoolPlug);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while reconciling plug {PlugId}", plugPoolPlug.PlugRtId);
            throw PlugReconsilerException.PlugReconcileFailed(plugPoolPlug.PlugRtId, e);
        }
    }

    /// <summary>
    /// Deletes the plugs for the plug pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    public async Task DeleteAsync(K8Pool k8Pool)
    {
        _logger.LogInformation("[{TenantId}] Deleting plug pool '{PoolName}', namespace '{Namespace}'", 
            k8Pool.TenantId, k8Pool.PoolName, k8Pool.Namespace);
        
        try
        {
            await DeleteAllPlugDeploymentsAsync(k8Pool);
            await DeleteAllPlugServicesAsync(k8Pool);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while deleting plug pool {PoolName}", k8Pool.PoolName);
            throw PlugReconsilerException.PoolDeleteFailed(k8Pool.PoolName, e);
        }
    }

    public async Task DeleteAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        _logger.LogInformation("[{TenantId}] Deleting plug '{PlugId}', namespace '{Namespace}'", 
            k8Pool.TenantId, plugPoolPlug.PlugRtId, k8Pool.Namespace);
        
        try
        {
            await DeletePlugDeploymentAsync(k8Pool, plugPoolPlug);
            await DeletePlugServiceAsync(k8Pool, plugPoolPlug);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while deleting plug {PlugId}", plugPoolPlug.PlugRtId);
            throw PlugReconsilerException.PlugDeleteFailed(plugPoolPlug.PlugRtId, e);
        }
    }

    private async Task DeletePlugServiceAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        _logger.LogInformation("[{TenantId}] Deleting plug service for plug '{PlugId}', namespace '{Namespace}'", 
            k8Pool.TenantId, plugPoolPlug.PlugRtId, k8Pool.Namespace);
        
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.List<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        var existingService = existingServices.SingleOrDefault();
        if (existingService != null)
        {
            await DeletePlugService(k8Pool, existingService);
        }
    }

    private async Task DeletePlugDeploymentAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        _logger.LogInformation("[{TenantId}] Deleting plug deployment for plug '{PlugId}', namespace '{Namespace}'", 
            k8Pool.TenantId, plugPoolPlug.PlugRtId, k8Pool.Namespace);
        
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingDeployments =
            await _kubernetesClient.List<V1Deployment>(k8Pool.Namespace, labelSelector: deploymentLabels.AsLabelSelector());

        var existingService = existingDeployments.SingleOrDefault();
        if (existingService != null)
        {
            await DeletePlugDeployment(k8Pool, existingService);
        }
    }

    private async Task DeleteAllPlugDeploymentsAsync(K8Pool k8Pool)
    {
        _logger.LogInformation("[{TenantId}] Deleting all plug deployments for pool '{PoolName}', namespace '{Namespace}'", 
            k8Pool.TenantId, k8Pool.PoolName, k8Pool.Namespace);
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };


        var existingDeployments =
            await _kubernetesClient.List<V1Deployment>(k8Pool.Namespace, labelSelector: deploymentLabels.AsLabelSelector());

        foreach (var existingDeployment in existingDeployments)
        {
            await DeletePlugDeployment(k8Pool, existingDeployment);
        }
    }

    private async Task DeletePlugDeployment(K8Pool k8Pool, V1Deployment existingDeployment)
    {
        _logger.LogInformation("[{TenantId}] Deleting plug deployment '{DeploymentName}' for pool '{PoolName}', namespace '{Namespace}'", 
            k8Pool.TenantId, existingDeployment.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        
        _logger.DeletingDeployment(existingDeployment.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetesClient.Delete<V1Deployment>(existingDeployment.Metadata.Name, k8Pool.Namespace);
    }

    private async Task DeleteAllPlugServicesAsync(K8Pool k8Pool)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.List<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        foreach (var existingService in existingServices)
        {
            await DeletePlugService(k8Pool, existingService);
        }
    }

    private async Task DeletePlugService(K8Pool k8Pool, V1Service existingService)
    {
        _logger.DeletingService(existingService.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetesClient.Delete<V1Service>(existingService.Metadata.Name, k8Pool.Namespace);
    }

    private async Task ReconcilePlugDeploymentAsync(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug)
    {
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = poolDescriptor.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = poolDescriptor.TenantId
        };

        var existingDeployments =
            await _kubernetesClient.List<V1Deployment>(poolDescriptor.Namespace, labelSelector: deploymentLabels.AsLabelSelector());

        if (existingDeployments.Any())
        {
            await DeletePlugDeployment(poolDescriptor, existingDeployments.Single());
        }

        await CreateDeployment(poolDescriptor, plugPoolPlug, deploymentLabels);
    }

    private async Task ReconcilePlugServiceAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices =
            await _kubernetesClient.List<V1Service>(k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        if (existingServices.Any())
        {
            await DeletePlugService(k8Pool, existingServices.Single());
        }

        await CreateService(k8Pool, plugPoolPlug, serviceLabels);
    }

    private async Task CreateDeployment(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug,
        Dictionary<string, string> deploymentLabels)
    {
        var deploymentName = $"{poolDescriptor.TenantId}-{plugPoolPlug.PlugRtId.ToString()}-octo-mesh-plug";
        var secretName = $"{poolDescriptor.TenantId}-{poolDescriptor.PoolName}-octo-mesh-connection";

        _logger.CreatingDeployment(deploymentName, poolDescriptor.PoolName, poolDescriptor.Namespace);

        var deploymentImageName = plugPoolPlug.ImageName + ":" + plugPoolPlug.Version;

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
                                Env = new Collection<V1EnvVar>
                                {
                                    new()
                                    {
                                        Name = "OCTO_PLUG__TENANTID",
                                        Value = poolDescriptor.TenantId
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__PLUGCONTROLLERSERVICESURI",
                                        Value = poolDescriptor.PlugControllerUri
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__PLUGID",
                                        Value = plugPoolPlug.PlugRtId.ToString()
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__BROKERHOST",
                                        Value = poolDescriptor.BrokerHost
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__BROKERVIRTUALHOST",
                                        Value = poolDescriptor.BrokerVirtualHost
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__BROKERPORT",
                                        Value = poolDescriptor.BrokerPort.ToString()
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__BROKERUSERNAME",
                                        ValueFrom = new V1EnvVarSource
                                        {
                                            SecretKeyRef = new V1SecretKeySelector
                                            {
                                                Name = secretName,
                                                Key = "brokerusername"
                                            }
                                        }
                                    },
                                    new()
                                    {
                                        Name = "OCTO_PLUG__BROKERPASSWORD",
                                        ValueFrom = new V1EnvVarSource
                                        {
                                            SecretKeyRef = new V1SecretKeySelector
                                            {
                                                Name = secretName,
                                                Key = "brokerpassword"
                                            }
                                        }
                                    },
                                },
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

        await _kubernetesClient.Create(deployment);
    }

    private async Task CreateService(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug,
        Dictionary<string, string> serviceLabels)
    {
        var serviceName = $"{k8Pool.TenantId}-{plugPoolPlug.PlugRtId}-octo-mesh-plug";

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

        await _kubernetesClient.Create(service);
    }
}