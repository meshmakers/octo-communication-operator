using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolDeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";

    [Test]
    public async Task PoolDeployedAsync_DelegatesToPoolManager()
    {
        await Service.PoolDeployedAsync(new DeployedPoolDto { TenantId = TenantId, PoolName = PoolName });

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolName);
    }

    [Test]
    public async Task PoolDeployedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        PoolManager.CreatePoolAsync(TenantId, PoolName)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolDeployedAsync(new DeployedPoolDto { TenantId = TenantId, PoolName = PoolName });

        await PoolManager.Received(1).CreatePoolAsync(TenantId, PoolName);
    }
}
