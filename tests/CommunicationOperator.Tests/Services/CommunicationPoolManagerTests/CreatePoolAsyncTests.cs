using k8s.Models;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public class CreatePoolAsyncTests : CommunicationPoolManagerTestsBase
{
    [Test]
    public async Task CreatePoolAsync_CrAlreadyExists_NoCallsToCreate()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolRtId);

        await Gateway.DidNotReceive().CreateCommunicationPoolAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreatePoolAsync_CrAndSecretMissing_BothCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolRtId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Any<V1Secret>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_SecretExists_OnlyCrCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolRtId);

        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_BrokerSecretCarriesCredentialsAndLabels()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolRtId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == ExpectedSecretName &&
            s.Metadata.NamespaceProperty == PoolNamespace &&
            s.Type == "Opaque" &&
            s.StringData["brokerusername"] == "octo" &&
            s.StringData["brokerpassword"] == "secret" &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/pool-rt-id"] == PoolRtId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/managed-by"] == "communication-operator"));
    }

    [Test]
    public async Task CreatePoolAsync_NullBrokerCredentials_StoredAsEmptyStrings()
    {
        OperatorOptions.BrokerUser = null;
        OperatorOptions.BrokerPassword = null;
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolRtId);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.StringData["brokerusername"] == string.Empty &&
            s.StringData["brokerpassword"] == string.Empty));
    }

    [Test]
    public async Task CreatePoolAsync_TenantIdIsLowercased()
    {
        // PoolRtId is already DNS-safe (24-char hex). TenantId can still
        // arrive with mixed case from CK; the sanitiser must lowercase it
        // so the resulting CR/Secret name is RFC 1123.
        const string poolRtId = "65d5c447b420da3fb12381bc";
        const string expectedCr = "mixedcase-65d5c447b420da3fb12381bc";
        const string expectedSecret = "mixedcase-65d5c447b420da3fb12381bc-octo-mesh-connection";
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, expectedCr).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, expectedSecret).Returns(false);

        await Manager.CreatePoolAsync("MixedCase", poolRtId);

        await Gateway.Received(1).CommunicationPoolExistsAsync(PoolNamespace, expectedCr);
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }
}
