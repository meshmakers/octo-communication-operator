using k8s.Models;
using KubeOps.Operator.Controller;
using KubeOps.Operator.Controller.Results;
using KubeOps.Operator.Finalizer;
using KubeOps.Operator.Rbac;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Finalizer;
using Meshmakers.Octo.Communication.Operator.Services;

namespace Meshmakers.Octo.Communication.Operator.Controller;

[EntityRbac(typeof(V1CommunicationPoolEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Namespace), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Deployment), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.All)]
public class CommunicationPoolController(
    ILogger<CommunicationPoolController> logger,
    IFinalizerManager<V1CommunicationPoolEntity> finalizerManager,
    IPoolService poolService)
    : IResourceController<V1CommunicationPoolEntity>
{
    public async Task<ResourceControllerResult?> ReconcileAsync(V1CommunicationPoolEntity entity)
    {
        logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await finalizerManager.RegisterFinalizerAsync<CommunicationPoolFinalizer>(entity);

        await poolService.RegisterPoolAsync(entity);
        
        return null;        
    }

    public Task StatusModifiedAsync(V1CommunicationPoolEntity entity)
    {
        logger.LogInformation("Entity {Name} called {StatusModifiedAsyncName}", entity.Name(), nameof(StatusModifiedAsync));

        return Task.CompletedTask;
    }

    public async Task DeletedAsync(V1CommunicationPoolEntity entity)
    {
        logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        await poolService.UnRegisterPoolAsync(entity);
    }
}
