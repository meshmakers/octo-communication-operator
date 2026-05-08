using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class PoolUndeployedAsyncTests : OperatorHubServiceTestsBase
{
    private const string PoolName = "default";

    [Test]
    public async Task PoolUndeployedAsync_DelegatesToPoolManager()
    {
        await Service.PoolUndeployedAsync(TenantId, PoolName);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolName);
    }

    [Test]
    public async Task PoolUndeployedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        PoolManager.DeletePoolAsync(TenantId, PoolName)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.PoolUndeployedAsync(TenantId, PoolName);

        await PoolManager.Received(1).DeletePoolAsync(TenantId, PoolName);
    }
}
