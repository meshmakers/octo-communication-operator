using k8s.Models;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public class CreatePoolAsyncTests : CommunicationPoolManagerTestsBase
{
    [Test]
    public async Task CreatePoolAsync_CrAlreadyExists_NoCallsToCreate()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolRtId, PoolName);

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

        await Manager.CreatePoolAsync(TenantId, PoolRtId, PoolName);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Any<V1Secret>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_SecretExists_OnlyCrCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolRtId, PoolName);

        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_BrokerSecretCarriesCredentialsAndLabels()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolRtId, PoolName);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == ExpectedSecretName &&
            s.Metadata.NamespaceProperty == PoolNamespace &&
            s.Type == "Opaque" &&
            s.StringData["brokerusername"] == "octo" &&
            s.StringData["brokerpassword"] == "secret" &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/pool-rt-id"] == PoolRtId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/managed-by"] == "communication-operator" &&
            s.Metadata.Annotations["octo-mesh.meshmakers.io/pool-name"] == PoolName));
    }

    [Test]
    public async Task CreatePoolAsync_NullBrokerCredentials_StoredAsEmptyStrings()
    {
        OperatorOptions.BrokerUser = null;
        OperatorOptions.BrokerPassword = null;
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolRtId, PoolName);

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

        await Manager.CreatePoolAsync("MixedCase", poolRtId, "MyPool");

        await Gateway.Received(1).CommunicationPoolExistsAsync(PoolNamespace, expectedCr);
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_PoolNameWithWhitespace_LandsInAnnotationOnly()
    {
        // sbeg + "Communication Pool" used to crash with apiserver 422 because
        // the secret name was "sbeg-communication pool-octo-mesh-connection"
        // (whitespace, mixed case) — invalid RFC 1123 subdomain. Now the
        // CR/secret name is derived from poolRtId (always DNS-safe), and
        // the user-facing pool name lands only in the annotation for
        // UI/debugging.
        const string poolRtId = "67e10c0bfe3e19891bbfd261";
        const string crName = "sbeg-67e10c0bfe3e19891bbfd261";
        const string secretName = "sbeg-67e10c0bfe3e19891bbfd261-octo-mesh-connection";
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, crName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, secretName).Returns(false);

        await Manager.CreatePoolAsync("sbeg", poolRtId, "Communication Pool");

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == secretName &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/pool-rt-id"] == poolRtId &&
            s.Metadata.Annotations["octo-mesh.meshmakers.io/pool-name"] == "Communication Pool"));
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }
}
