using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class CommunicationPoolValidatorTests
{
    private readonly CommunicationPoolValidator _validator = new();

    [Test]
    public async Task Create_PoolNameWithoutSpaces_ReturnsValid()
    {
        var entity = NewEntity("default");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Create_PoolNameWithSpace_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity("invalid name");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    private static V1CommunicationPoolEntity NewEntity(string poolName) =>
        new()
        {
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                PoolName = poolName
            }
        };
}
