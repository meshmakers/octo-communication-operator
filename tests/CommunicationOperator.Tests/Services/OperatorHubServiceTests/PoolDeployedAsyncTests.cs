using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolDeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";
    private const string PoolRtId = "65d5c447b420da3fb12381bc";

    private static DeployedPoolDto Pool() => new()
    {
        TenantId = TenantId, PoolRtId = PoolRtId,
    };

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsEnabled_DelegatesToPoolManager()
    {
        OperatorOptions.AutoManagePools = true;

        await Service.PoolDeployedAsync(Pool());

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolRtId);
    }

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsDisabled_SkipsPoolManager()
    {
        // Edge operators receive the same PoolDeployedAsync broadcast as the central
        // operator (the controller fans out to every connected operator) but must NOT
        // auto-create CRs — edge CRs are managed manually or by an external system.
        OperatorOptions.AutoManagePools = false;

        await Service.PoolDeployedAsync(Pool());

        await PoolManager.DidNotReceiveWithAnyArgs().CreatePoolAsync(default!, default!);
    }

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsEnabledAndPoolManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManagePools = true;
        PoolManager.CreatePoolAsync(TenantId, PoolRtId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolDeployedAsync(Pool());

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolRtId);
    }
}
