using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class DeploymentSiteUndeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string DeploymentSiteName = "default";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381bc";

    [Test]
    public async Task DeploymentSiteUndeployedAsync_AutoManageDeploymentSitesEnabled_DelegatesToDeploymentSiteManager()
    {
        OperatorOptions.AutoManageDeploymentSites = true;

        await Service.DeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId);

        await DeploymentSiteManager.Received(1).DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
    }

    [Test]
    public async Task DeploymentSiteUndeployedAsync_AutoManageDeploymentSitesDisabled_SkipsDeploymentSiteManager()
    {
        // Symmetric to DeploymentSiteDeployedAsync: edge operators must ignore the broadcast.
        OperatorOptions.AutoManageDeploymentSites = false;

        await Service.DeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId);

        await DeploymentSiteManager.DidNotReceiveWithAnyArgs().DeleteDeploymentSiteAsync(default!, default!);
    }

    [Test]
    public async Task DeploymentSiteUndeployedAsync_AutoManageDeploymentSitesEnabledAndDeploymentSiteManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        DeploymentSiteManager.DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.DeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId);

        await DeploymentSiteManager.Received(1).DeleteDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
    }
}
