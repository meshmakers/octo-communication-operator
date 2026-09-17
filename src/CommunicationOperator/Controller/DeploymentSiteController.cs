using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Services;

namespace Meshmakers.Octo.Communication.Operator.Controller;

[EntityRbac(typeof(V1DeploymentSiteEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.All)]
public class DeploymentSiteController(
    ILogger<DeploymentSiteController> logger,
    IKubernetesClient client,
    IDeploymentSiteService deploymentSiteService)
    : IEntityController<V1DeploymentSiteEntity>
{
    public async Task<ReconciliationResult<V1DeploymentSiteEntity>> ReconcileAsync(V1DeploymentSiteEntity entity,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));

        try
        {
            entity.Status.CommunicationStatus = "In Progress";
            entity = await client.UpdateStatusAsync(entity, cancellationToken);

            await deploymentSiteService.RegisterDeploymentSiteAsync(entity, cancellationToken);

            entity.Status.CommunicationStatus = "Registered";
            await client.UpdateStatusAsync(entity, cancellationToken);

            return ReconciliationResult<V1DeploymentSiteEntity>.Success(entity);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while reconciling entity {Name}", entity.Name());
            entity.Status.CommunicationStatus = "Failed: " + e.Message;
            await client.UpdateStatusAsync(entity, cancellationToken);

            return ReconciliationResult<V1DeploymentSiteEntity>.Failure(entity, "Failed to reconcile: " + e.Message,
                e, new TimeSpan(0, 1, 0));
        }
    }

    public async Task<ReconciliationResult<V1DeploymentSiteEntity>> DeletedAsync(V1DeploymentSiteEntity entity,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        // The CR is already gone from the cluster when DeletedAsync fires, so
        // any UpdateStatusAsync call would 404 and the KubeOps queue would
        // retry forever. Only run the in-process cleanup here.
        try
        {
            await deploymentSiteService.UnRegisterDeploymentSiteAsync(entity);
            return ReconciliationResult<V1DeploymentSiteEntity>.Success(entity);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while unregistering deployment site {Name}", entity.Name());
            return ReconciliationResult<V1DeploymentSiteEntity>.Failure(entity, "Failed to unregister: " + e.Message);
        }
    }
}