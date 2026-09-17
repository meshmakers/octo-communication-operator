using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Finalizer;

public class DeploymentSiteFinalizer(ILogger<DeploymentSiteFinalizer> logger)
    : IEntityFinalizer<V1DeploymentSiteEntity>
{
    public Task<ReconciliationResult<V1DeploymentSiteEntity>> FinalizeAsync(V1DeploymentSiteEntity entity, CancellationToken cancellationToken)
    {
        logger.LogInformation("Entity {Name} called {FinalizeAsyncName}", entity.Name(), nameof(FinalizeAsync));

        return Task.FromResult(ReconciliationResult<V1DeploymentSiteEntity>.Success(entity));
    }
}
