using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolUndeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsEnabled_DelegatesToPoolManager()
    {
        OperatorOptions.AutoManagePools = true;

        await Service.PoolUndeployedAsync(TenantId, PoolName);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolName);
    }

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsDisabled_SkipsPoolManager()
    {
        // Symmetric to PoolDeployedAsync: edge operators must ignore the broadcast.
        OperatorOptions.AutoManagePools = false;

        await Service.PoolUndeployedAsync(TenantId, PoolName);

        await PoolManager.DidNotReceiveWithAnyArgs().DeletePoolAsync(default!, default!);
    }

    [Test]
    public async Task PoolUndeployedAsync_AutoManagePoolsEnabledAndPoolManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManagePools = true;
        PoolManager.DeletePoolAsync(TenantId, PoolName)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolUndeployedAsync(TenantId, PoolName);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolName);
    }
}
