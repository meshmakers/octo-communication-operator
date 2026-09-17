using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class DeploymentSiteDeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string DeploymentSiteName = "default";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381bc";

    private static DeployedDeploymentSiteDto DeploymentSite() => new()
    {
        TenantId = TenantId, DeploymentSiteRtId = DeploymentSiteRtId,
    };

    [Test]
    public async Task DeploymentSiteDeployedAsync_AutoManageDeploymentSitesEnabled_DelegatesToDeploymentSiteManager()
    {
        OperatorOptions.AutoManageDeploymentSites = true;

        await Service.DeploymentSiteDeployedAsync(DeploymentSite());

        await DeploymentSiteManager.Received(1).CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
    }

    [Test]
    public async Task DeploymentSiteDeployedAsync_AutoManageDeploymentSitesDisabled_SkipsDeploymentSiteManager()
    {
        // Edge operators receive the same DeploymentSiteDeployedAsync broadcast as the central
        // operator (the controller fans out to every connected operator) but must NOT
        // auto-create CRs — edge CRs are managed manually or by an external system.
        OperatorOptions.AutoManageDeploymentSites = false;

        await Service.DeploymentSiteDeployedAsync(DeploymentSite());

        await DeploymentSiteManager.DidNotReceiveWithAnyArgs().CreateDeploymentSiteAsync(default!, default!);
    }

    [Test]
    public async Task DeploymentSiteDeployedAsync_AutoManageDeploymentSitesEnabledAndDeploymentSiteManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManageDeploymentSites = true;
        DeploymentSiteManager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.DeploymentSiteDeployedAsync(DeploymentSite());

        await DeploymentSiteManager.Received(1).CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);
    }
}
