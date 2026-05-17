using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolDeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsEnabled_DelegatesToPoolManager()
    {
        OperatorOptions.AutoManagePools = true;

        await Service.PoolDeployedAsync(new DeployedPoolDto { TenantId = TenantId, PoolName = PoolName });

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolName);
    }

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsDisabled_SkipsPoolManager()
    {
        // Edge operators receive the same PoolDeployedAsync broadcast as the central
        // operator (the controller fans out to every connected operator) but must NOT
        // auto-create CRs — edge CRs are managed manually or by an external system.
        OperatorOptions.AutoManagePools = false;

        await Service.PoolDeployedAsync(new DeployedPoolDto { TenantId = TenantId, PoolName = PoolName });

        await PoolManager.DidNotReceiveWithAnyArgs().CreatePoolAsync(default!, default!);
    }

    [Test]
    public async Task PoolDeployedAsync_AutoManagePoolsEnabledAndPoolManagerThrows_ExceptionIsSwallowed()
    {
        OperatorOptions.AutoManagePools = true;
        PoolManager.CreatePoolAsync(TenantId, PoolName)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolDeployedAsync(new DeployedPoolDto { TenantId = TenantId, PoolName = PoolName });

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolName);
    }
}
