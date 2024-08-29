using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Services;

namespace Meshmakers.Octo.Communication.Operator.Controller;

[EntityRbac(typeof(V1CommunicationPoolEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Namespace), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.All)]
public class CommunicationPoolController(
    ILogger<CommunicationPoolController> logger, IKubernetesClient client,
    IPoolService poolService)
    : IEntityController<V1CommunicationPoolEntity>
{
    public async Task ReconcileAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));

        try
        {
            entity.Status.CommunicationStatus = "In Progress";
            entity = await client.UpdateStatusAsync(entity, cancellationToken);

            await poolService.RegisterPoolAsync(entity, cancellationToken);
            
            entity.Status.CommunicationStatus = "Registered";
            await client.UpdateStatusAsync(entity, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while reconciling entity {Name}", entity.Name());
            entity.Status.CommunicationStatus = "Failed: " + e.Message;
            await client.UpdateStatusAsync(entity, cancellationToken);
            throw;
        }
    }

    public async Task DeletedAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        try
        {
            entity.Status.CommunicationStatus = "In Progress";
            entity = await client.UpdateStatusAsync(entity, cancellationToken);
            
            await poolService.UnRegisterPoolAsync(entity);
            
            entity.Status.CommunicationStatus = "Unregistered";
            await client.UpdateStatusAsync(entity, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while reconciling entity {Name}", entity.Name());
            entity.Status.CommunicationStatus = "Failed: " + e.Message;
            await client.UpdateStatusAsync(entity, cancellationToken);
            throw;
        }
    }
}
