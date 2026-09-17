using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class DeploymentSiteMutatorTests
{
    private readonly DeploymentSiteMutator _mutator = new();

    [Test]
    public async Task Create_AlwaysReturnsNoChanges()
    {
        var entity = new V1DeploymentSiteEntity
        {
            Spec = new V1DeploymentSiteEntity.V1DeploymentSiteEntitySpec
            {
            }
        };

        var result = _mutator.Create(entity, dryRun: false);

        await Assert.That(result.ModifiedObject).IsNull();
    }
}
