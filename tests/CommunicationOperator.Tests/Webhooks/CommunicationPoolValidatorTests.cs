using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class CommunicationPoolValidatorTests
{
    private const string ValidRtId = "6ad562f3ff7c40ff80275b84";

    private readonly CommunicationPoolValidator _validator = new();

    [Test]
    public async Task Create_ValidRtId_ReturnsValid()
    {
        // PoolRtId is the canonical pool identity. The validator does not
        // require any other field — TenantId is just a routing key on the
        // wire and the controller rejects unknown tenants with its own
        // typed exception.
        var entity = NewEntity(poolRtId: ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Create_EmptyPoolRtId_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(poolRtId: string.Empty);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_PoolRtIdTooShort_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(poolRtId: "deadbeef");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_PoolRtIdWithUppercase_ReturnsInvalidWithBadRequest()
    {
        // ObjectIds are case-sensitive on the wire and the controller
        // expects lowercase hex; uppercase digits would slip past a
        // case-insensitive regex but break downstream comparisons.
        var entity = NewEntity(poolRtId: "6AD562F3FF7C40FF80275B84");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_PoolRtIdWithNonHexChar_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(poolRtId: "6ad562f3ff7c40ff80275b8z");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Update_ValidRtId_ReturnsValid()
    {
        var oldEntity = NewEntity(poolRtId: ValidRtId);
        var newEntity = NewEntity(poolRtId: ValidRtId);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Update_NewSpecHasEmptyPoolRtId_ReturnsInvalidWithBadRequest()
    {
        // Same rule on update as on create — a kubectl edit that wipes
        // poolRtId is just as broken as a fresh CR with poolRtId empty.
        var oldEntity = NewEntity(poolRtId: ValidRtId);
        var newEntity = NewEntity(poolRtId: string.Empty);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    private static V1CommunicationPoolEntity NewEntity(string poolRtId) =>
        new()
        {
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                PoolRtId = poolRtId
            }
        };
}
