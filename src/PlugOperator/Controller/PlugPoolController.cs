using k8s.Models;
using KubeOps.Operator.Controller;
using KubeOps.Operator.Controller.Results;
using KubeOps.Operator.Finalizer;
using KubeOps.Operator.Rbac;
using PlugOperator.Entities;
using PlugOperator.Finalizer;
using PlugOperator.Services;

namespace PlugOperator.Controller;

[EntityRbac(typeof(V1PlugPoolEntity), Verbs = RbacVerb.All)]
public class PlugPoolController : IResourceController<V1PlugPoolEntity>
{
    private readonly IPlugPoolService _plugPoolService;
    private readonly ILogger<PlugPoolController> _logger;
    private readonly IFinalizerManager<V1PlugPoolEntity> _finalizerManager;

    public PlugPoolController(ILogger<PlugPoolController> logger, IFinalizerManager<V1PlugPoolEntity> finalizerManager, IPlugPoolService plugPoolService)
    {
        _plugPoolService = plugPoolService;
        _logger = logger;
        _finalizerManager = finalizerManager;
    }

    public async Task<ResourceControllerResult?> ReconcileAsync(V1PlugPoolEntity entity)
    {
        _logger.LogInformation("Entity {Name} called {ReconcileAsyncName}", entity.Name(), nameof(ReconcileAsync));
        await _finalizerManager.RegisterFinalizerAsync<DemoFinalizer>(entity);

        await _plugPoolService.RegisterPoolAsync(entity);
        
        return null;        
    }

    public Task StatusModifiedAsync(V1PlugPoolEntity entity)
    {
        _logger.LogInformation("Entity {Name} called {StatusModifiedAsyncName}", entity.Name(), nameof(StatusModifiedAsync));

        return Task.CompletedTask;
    }

    public async Task DeletedAsync(V1PlugPoolEntity entity)
    {
        _logger.LogInformation("Entity {Name} called {DeletedAsyncName}", entity.Name(), nameof(DeletedAsync));

        await _plugPoolService.UnRegisterPoolAsync(entity);
    }
}
