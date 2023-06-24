using k8s.Models;
using KubeOps.Operator.Finalizer;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Finalizer;

public class CommunicationPoolFinalizer : IResourceFinalizer<V1CommunicationPoolEntity>
{
    private readonly ILogger<CommunicationPoolFinalizer> _logger;

    public CommunicationPoolFinalizer(ILogger<CommunicationPoolFinalizer> logger)
    {
        _logger = logger;
    }

    public Task FinalizeAsync(V1CommunicationPoolEntity entity)
    {
        _logger.LogInformation("entity {Name} called {FinalizeAsyncName}", entity.Name(), nameof(FinalizeAsync));

        return Task.CompletedTask;
    }
}
