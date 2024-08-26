using k8s.Models;
using KubeOps.Abstractions.Finalizer;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Finalizer;

public class CommunicationPoolFinalizer(ILogger<CommunicationPoolFinalizer> logger)
    : IEntityFinalizer<V1CommunicationPoolEntity>
{
    public Task FinalizeAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {FinalizeAsyncName}", entity.Name(), nameof(FinalizeAsync));

        return Task.CompletedTask;
    }
}
