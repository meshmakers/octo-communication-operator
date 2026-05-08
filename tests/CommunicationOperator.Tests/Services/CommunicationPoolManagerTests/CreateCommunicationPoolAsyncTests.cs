using k8s.Models;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public class CreateCommunicationPoolAsyncTests : CommunicationPoolManagerTestsBase
{
    [Test]
    public async Task CreateCommunicationPoolAsync_CrAlreadyExists_NoCallsToCreate()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);

        await Manager.CreateCommunicationPoolAsync(TenantId);

        await Gateway.DidNotReceive().CreateCommunicationPoolAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateCommunicationPoolAsync_CrAndSecretMissing_BothCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateCommunicationPoolAsync(TenantId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Any<V1Secret>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreateCommunicationPoolAsync_SecretExists_OnlyCrCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(true);

        await Manager.CreateCommunicationPoolAsync(TenantId);

        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreateCommunicationPoolAsync_BrokerSecretCarriesCredentialsAndLabels()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateCommunicationPoolAsync(TenantId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == ExpectedSecretName &&
            s.Metadata.NamespaceProperty == PoolNamespace &&
            s.Type == "Opaque" &&
            s.StringData["brokerusername"] == "octo" &&
            s.StringData["brokerpassword"] == "secret" &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/managed-by"] == "communication-operator"));
    }

    [Test]
    public async Task CreateCommunicationPoolAsync_NullBrokerCredentials_StoredAsEmptyStrings()
    {
        OperatorOptions.BrokerUser = null;
        OperatorOptions.BrokerPassword = null;
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateCommunicationPoolAsync(TenantId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.StringData["brokerusername"] == string.Empty &&
            s.StringData["brokerpassword"] == string.Empty));
    }

    [Test]
    public async Task CreateCommunicationPoolAsync_CrIsLowercased()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, "mixedcase-default").Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, "mixedcase-default-octo-mesh-connection").Returns(false);

        await Manager.CreateCommunicationPoolAsync("MixedCase");

        await Gateway.Received(1).CommunicationPoolExistsAsync(PoolNamespace, "mixedcase-default");
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }
}
