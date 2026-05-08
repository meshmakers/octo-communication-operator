using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class TenantCreatedAsyncTests : OperatorHubServiceTestsBase
{
    [Test]
    public async Task TenantCreatedAsync_DelegatesToPoolManager()
    {
        await Service.TenantCreatedAsync(TenantId);

        await PoolManager.Received(1).CreateCommunicationPoolAsync(TenantId);
    }

    [Test]
    public async Task TenantCreatedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        PoolManager.CreateCommunicationPoolAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.TenantCreatedAsync(TenantId);

        await PoolManager.Received(1).CreateCommunicationPoolAsync(TenantId);
    }
}
