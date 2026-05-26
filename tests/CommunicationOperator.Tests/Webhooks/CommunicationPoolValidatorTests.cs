using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class CommunicationPoolValidatorTests
{
    private const string ValidRtId = "6ad562f3ff7c40ff80275b84";

    private readonly CommunicationPoolValidator _validator = new();

    [Test]
    public async Task Create_ValidSpec_ReturnsValid()
    {
        var entity = NewEntity("default", ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Create_PoolNameWithSpace_ReturnsValid()
    {
        // Spec.PoolName now carries the unsanitised user-friendly value;
        // every derived k8s name is built from PoolRtId instead, so the
        // apiserver no longer cares about whitespace. The webhook only
        // rejects empty / whitespace-only names (those would break the
        // controller-side dictionary lookup).
        var entity = NewEntity("Default Cloud", ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Create_EmptyPoolName_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(string.Empty, ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_WhitespaceOnlyPoolName_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity("   ", ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_EmptyPoolRtId_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity("default", string.Empty);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_PoolRtIdTooShort_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity("default", "deadbeef");

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
        var entity = NewEntity("default", "6AD562F3FF7C40FF80275B84");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_PoolRtIdWithNonHexChar_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity("default", "6ad562f3ff7c40ff80275b8z");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Update_ValidSpec_ReturnsValid()
    {
        var oldEntity = NewEntity("default", ValidRtId);
        var newEntity = NewEntity("default", ValidRtId);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Update_NewSpecHasEmptyPoolRtId_ReturnsInvalidWithBadRequest()
    {
        // Same rule on update as on create — a kubectl edit that wipes
        // poolRtId is just as broken as a fresh CR with poolRtId empty.
        var oldEntity = NewEntity("default", ValidRtId);
        var newEntity = NewEntity("default", string.Empty);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    private static V1CommunicationPoolEntity NewEntity(string poolName, string poolRtId) =>
        new()
        {
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                PoolName = poolName,
                PoolRtId = poolRtId
            }
        };
}
