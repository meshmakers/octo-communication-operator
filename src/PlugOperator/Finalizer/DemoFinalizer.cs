using k8s.Models;
using KubeOps.Operator.Finalizer;
using PlugOperator.Entities;

namespace PlugOperator.Finalizer;

public class DemoFinalizer : IResourceFinalizer<V1PlugPoolEntity>
{
    private readonly ILogger<DemoFinalizer> _logger;

    public DemoFinalizer(ILogger<DemoFinalizer> logger)
    {
        _logger = logger;
    }

    public Task FinalizeAsync(V1PlugPoolEntity entity)
    {
        _logger.LogInformation($"entity {entity.Name()} called {nameof(FinalizeAsync)}.");

        return Task.CompletedTask;
    }
}
