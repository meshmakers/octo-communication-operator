using k8s.Models;
using KubeOps.Operator.Finalizer;
using PlugOperator.Entities;

namespace PlugOperator.Finalizer;

public class PlugPoolFinalizer : IResourceFinalizer<V1PlugPoolEntity>
{
    private readonly ILogger<PlugPoolFinalizer> _logger;

    public PlugPoolFinalizer(ILogger<PlugPoolFinalizer> logger)
    {
        _logger = logger;
    }

    public Task FinalizeAsync(V1PlugPoolEntity entity)
    {
        _logger.LogInformation($"entity {entity.Name()} called {nameof(FinalizeAsync)}.");

        return Task.CompletedTask;
    }
}
