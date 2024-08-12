using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Rbac;
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
    ILogger<CommunicationPoolController> logger,
    IPoolService poolService)
    : IEntityController<V1CommunicationPoolEntity>
{
    public async Task ReconcileAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));

        await poolService.RegisterPoolAsync(entity);
    }

    public async Task DeletedAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        await poolService.UnRegisterPoolAsync(entity);
    }
}
