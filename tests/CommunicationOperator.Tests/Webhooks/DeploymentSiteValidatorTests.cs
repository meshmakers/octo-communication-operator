using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Webhooks;

namespace Meshmakers.Octo.Communication.Operator.Tests.Webhooks;

public class DeploymentSiteValidatorTests
{
    private const string ValidRtId = "6ad562f3ff7c40ff80275b84";

    private readonly DeploymentSiteValidator _validator = new();

    [Test]
    public async Task Create_ValidRtId_ReturnsValid()
    {
        // DeploymentSiteRtId is the canonical deployment site identity. The validator does not
        // require any other field — TenantId is just a routing key on the
        // wire and the controller rejects unknown tenants with its own
        // typed exception.
        var entity = NewEntity(deploymentSiteRtId: ValidRtId);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Create_EmptyDeploymentSiteRtId_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(deploymentSiteRtId: string.Empty);

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_DeploymentSiteRtIdTooShort_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(deploymentSiteRtId: "deadbeef");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_DeploymentSiteRtIdWithUppercase_ReturnsInvalidWithBadRequest()
    {
        // ObjectIds are case-sensitive on the wire and the controller
        // expects lowercase hex; uppercase digits would slip past a
        // case-insensitive regex but break downstream comparisons.
        var entity = NewEntity(deploymentSiteRtId: "6AD562F3FF7C40FF80275B84");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Create_DeploymentSiteRtIdWithNonHexChar_ReturnsInvalidWithBadRequest()
    {
        var entity = NewEntity(deploymentSiteRtId: "6ad562f3ff7c40ff80275b8z");

        var result = _validator.Create(entity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Update_ValidRtId_ReturnsValid()
    {
        var oldEntity = NewEntity(deploymentSiteRtId: ValidRtId);
        var newEntity = NewEntity(deploymentSiteRtId: ValidRtId);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsTrue();
    }

    [Test]
    public async Task Update_NewSpecHasEmptyDeploymentSiteRtId_ReturnsInvalidWithBadRequest()
    {
        // Same rule on update as on create — a kubectl edit that wipes
        // deploymentSiteRtId is just as broken as a fresh CR with deploymentSiteRtId empty.
        var oldEntity = NewEntity(deploymentSiteRtId: ValidRtId);
        var newEntity = NewEntity(deploymentSiteRtId: string.Empty);

        var result = _validator.Update(oldEntity, newEntity, dryRun: false);

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Status?.Code).IsEqualTo(400);
    }

    private static V1DeploymentSiteEntity NewEntity(string deploymentSiteRtId) =>
        new()
        {
            Spec = new V1DeploymentSiteEntity.V1DeploymentSiteEntitySpec
            {
                DeploymentSiteRtId = deploymentSiteRtId
            }
        };
}
