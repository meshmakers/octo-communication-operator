using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.OperatorHubServiceTests;

public class TenantDeletedAsyncTests : OperatorHubServiceTestsBase
{
    [Test]
    public async Task TenantDeletedAsync_DelegatesToPoolManager()
    {
        await Service.TenantDeletedAsync(TenantId);

        await PoolManager.Received(1).DeleteCommunicationPoolAsync(TenantId);
    }

    [Test]
    public async Task TenantDeletedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        PoolManager.DeleteCommunicationPoolAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Service.TenantDeletedAsync(TenantId);

        await PoolManager.Received(1).DeleteCommunicationPoolAsync(TenantId);
    }
}
