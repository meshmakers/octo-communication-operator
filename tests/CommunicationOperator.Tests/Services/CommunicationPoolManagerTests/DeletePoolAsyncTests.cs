using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public class DeletePoolAsyncTests : CommunicationPoolManagerTestsBase
{
    [Test]
    public async Task DeletePoolAsync_CrDoesNotExist_NothingDeleted()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);

        await Manager.DeletePoolAsync(TenantId, PoolName);

        await Gateway.DidNotReceive().DeleteCommunicationPoolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeletePoolAsync_CrAndSecretExist_BothDeleted()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(true);

        await Manager.DeletePoolAsync(TenantId, PoolName);

        await Gateway.Received(1).DeleteCommunicationPoolAsync(PoolNamespace, ExpectedCrName);
        await Gateway.Received(1).DeleteSecretAsync(PoolNamespace, ExpectedSecretName);
    }

    [Test]
    public async Task DeletePoolAsync_SecretMissing_OnlyCrDeleted()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.DeletePoolAsync(TenantId, PoolName);

        await Gateway.Received(1).DeleteCommunicationPoolAsync(PoolNamespace, ExpectedCrName);
        await Gateway.DidNotReceive().DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
