using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class CommunicationPoolMutatorTests
{
    private readonly CommunicationPoolMutator _mutator = new();

    [Test]
    public async Task Create_AlwaysReturnsNoChanges()
    {
        var entity = new V1CommunicationPoolEntity
        {
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                PoolName = "default"
            }
        };

        var result = _mutator.Create(entity, dryRun: false);

        await Assert.That(result.ModifiedObject).IsNull();
    }
}
