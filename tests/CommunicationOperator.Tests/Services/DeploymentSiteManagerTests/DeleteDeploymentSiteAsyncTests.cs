using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.DeploymentSiteManagerTests;

public class DeleteDeploymentSiteAsyncTests : DeploymentSiteManagerTestsBase
{
    [Test]
    public async Task DeleteDeploymentSiteAsync_CrDoesNotExist_NothingDeleted()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(false);

        await Manager.DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.DidNotReceive().DeleteDeploymentSiteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteDeploymentSiteAsync_CrAndSecretExist_BothDeleted()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(true);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(true);

        await Manager.DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.Received(1).DeleteDeploymentSiteAsync(DeploymentSiteNamespace, ExpectedCrName);
        await Gateway.Received(1).DeleteSecretAsync(DeploymentSiteNamespace, ExpectedSecretName);
    }

    [Test]
    public async Task DeleteDeploymentSiteAsync_SecretMissing_OnlyCrDeleted()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(true);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(false);

        await Manager.DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.Received(1).DeleteDeploymentSiteAsync(DeploymentSiteNamespace, ExpectedCrName);
        await Gateway.DidNotReceive().DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
