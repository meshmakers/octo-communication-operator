using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolUndeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";
    private const string PoolRtId = "65d5c447b420da3fb12381bc";

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsEnabled_DelegatesToPoolManager()
    {
        OperatorOptions.AutoManagePools = true;

        await Service.PoolUndeployedAsync(TenantId, PoolRtId);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolRtId);
    }

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsDisabled_SkipsPoolManager()
    {
        // Symmetric to PoolDeployedAsync: edge operators must ignore the broadcast.
        OperatorOptions.AutoManagePools = false;

        await Service.PoolUndeployedAsync(TenantId, PoolRtId);

        await PoolManager.DidNotReceiveWithAnyArgs().DeletePoolAsync(default!, default!);
    }

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsEnabledAndPoolManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManagePools = true;
        PoolManager.DeletePoolAsync(TenantId, PoolRtId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolUndeployedAsync(TenantId, PoolRtId);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolRtId);
    }
}
