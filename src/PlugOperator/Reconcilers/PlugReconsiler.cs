using System.Collections.ObjectModel;
using k8s;
using k8s.Models;
using KubeOps.Operator.Entities.Extensions;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using PlugOperator.Common;
using PlugOperator.Entities;
using PlugOperator.Models;

namespace PlugOperator.Reconcilers;

public class PlugReconciler : IPlugReconciler
{
    private readonly IKubernetes _kubernetes;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PlugReconciler"/>
    /// </summary>
    /// <param name="kubernetes">Kubernetes client to use</param>
    /// <param name="logger">Logger to write log message to</param>
    public PlugReconciler(IKubernetes kubernetes, ILogger<PlugReconciler> logger)
    {
        _logger = logger;
        _kubernetes = kubernetes;
    }

    /// <summary>
    /// Reconciles the plugs for the plug pool resource.
    /// </summary>
    /// <param name="poolDescriptor">Meta data about the pool</param>
    /// <param name="plugPoolPlug">The pool plug to reconcile</param>
    /// <param name="entity">Plug pool entity for reconcile</param>
    public async Task ReconcileAsync(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity)
    {
        await ReconcilePlugDeploymentAsync(poolDescriptor, plugPoolPlug, entity);
        await ReconcilePlugServiceAsync(poolDescriptor, plugPoolPlug, entity);
    }

    /// <summary>
    /// Deletes the plugs for the plug pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    public async Task DeleteAsync(K8Pool k8Pool)
    {
        await DeleteAllPlugDeploymentsAsync(k8Pool);
        await DeleteAllPlugServicesAsync(k8Pool);
    }

    public async Task DeleteAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        await DeletePlugDeploymentAsync(k8Pool, plugPoolPlug);
        await DeletePlugServiceAsync(k8Pool, plugPoolPlug);
    }

    private async Task DeletePlugServiceAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices = await _kubernetes.ListNamespacedServiceAsync(
            k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        var existingService = existingServices.Items.SingleOrDefault();
        if (existingService != null)
        {
            await DeletePlugService(k8Pool, existingService);
        }
    }
    
    private async Task DeletePlugDeploymentAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices = await _kubernetes.ListNamespacedDeploymentAsync(
            k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        var existingService = existingServices.Items.SingleOrDefault();
        if (existingService != null)
        {
            await DeletePlugDeployment(k8Pool, existingService);
        }
    }

    private async Task DeleteAllPlugDeploymentsAsync(K8Pool k8Pool)
    {
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingDeployments = await _kubernetes.ListNamespacedDeploymentAsync(
            k8Pool.Namespace, labelSelector: deploymentLabels.AsLabelSelector());

        foreach (var existingDeployment in existingDeployments.Items)
        {
            await DeletePlugDeployment(k8Pool, existingDeployment);
        }
    }

    private async Task DeletePlugDeployment(K8Pool k8Pool, V1Deployment existingDeployment)
    {
        _logger.DeletingDeployment(existingDeployment.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetes.DeleteNamespacedDeploymentAsync(existingDeployment.Metadata.Name, k8Pool.Namespace);
    }

    private async Task DeleteAllPlugServicesAsync(K8Pool k8Pool)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };

        var existingServices = await _kubernetes.ListNamespacedServiceAsync(
            k8Pool.Namespace, labelSelector: serviceLabels.AsLabelSelector());

        foreach (var existingService in existingServices.Items)
        {
            await DeletePlugService(k8Pool, existingService);
        }
    }

    private async Task DeletePlugService(K8Pool k8Pool, V1Service existingService)
    {
        _logger.DeletingService(existingService.Metadata.Name, k8Pool.PoolName, k8Pool.Namespace);
        await _kubernetes.DeleteNamespacedServiceAsync(existingService.Metadata.Name, k8Pool.Namespace);
    }

    private async Task ReconcilePlugDeploymentAsync(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity)
    {
        var deploymentLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = poolDescriptor.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = poolDescriptor.TenantId
        };

        var existingDeployments = await _kubernetes.ListNamespacedDeploymentAsync(
            poolDescriptor.Namespace, labelSelector: deploymentLabels.AsLabelSelector());

        if (existingDeployments.Items.Any())
        {
            await DeletePlugDeployment(poolDescriptor, existingDeployments.Items.Single());
        }

        await CreateDeployment(poolDescriptor, plugPoolPlug, entity, deploymentLabels);
    }

    private async Task ReconcilePlugServiceAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity)
    {
        var serviceLabels = new Dictionary<string, string>
        {
            ["octo-mesh.meshmakers.io/component"] = "octo-mesh-plug",
            ["octo-mesh.meshmakers.io/plug"] = plugPoolPlug.PlugRtId.ToString(),
            ["octo-mesh.meshmakers.io/plug-pool"] = k8Pool.PoolName,
            ["octo-mesh.meshmakers.io/tenant"] = k8Pool.TenantId
        };


        var existingServices = await _kubernetes.ListNamespacedServiceAsync(
            entity.Namespace(), labelSelector: serviceLabels.AsLabelSelector());

        if (existingServices.Items.Any())
        {
            await DeletePlugService(k8Pool, existingServices.Items.Single());
        }

        await CreateService(k8Pool, plugPoolPlug, entity, serviceLabels);
    }

    private async Task CreateDeployment(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity,
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

        await _kubernetes.CreateNamespacedDeploymentAsync(
            deployment.WithOwnerReference(entity), poolDescriptor.Namespace);
    }

    private async Task CreateService(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity,
        Dictionary<string, string> serviceLabels)
    {
        var serviceName = $"{k8Pool.TenantId}-{plugPoolPlug.PlugRtId}-octo-mesh-plug";

        _logger.CreatingService(serviceName, entity.Name(), entity.Namespace());

        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName,
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

        await _kubernetes.CreateNamespacedServiceAsync(
            service.WithOwnerReference(entity),
            entity.Namespace());
    }
}