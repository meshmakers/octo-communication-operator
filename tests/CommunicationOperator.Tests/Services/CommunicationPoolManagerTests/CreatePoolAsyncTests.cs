using k8s.Models;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public class CreatePoolAsyncTests : CommunicationPoolManagerTestsBase
{
    [Test]
    public async Task CreatePoolAsync_CrAlreadyExists_NoCallsToCreate()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolName);

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

        await Manager.CreatePoolAsync(TenantId, PoolName);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Any<V1Secret>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_SecretExists_OnlyCrCreated()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(true);

        await Manager.CreatePoolAsync(TenantId, PoolName);

        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_BrokerSecretCarriesCredentialsAndLabels()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreatePoolAsync(TenantId, PoolName);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == ExpectedSecretName &&
            s.Metadata.NamespaceProperty == PoolNamespace &&
            s.Type == "Opaque" &&
            s.StringData["brokerusername"] == "octo" &&
            s.StringData["brokerpassword"] == "secret" &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/pool"] == PoolName &&
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

        await Manager.CreatePoolAsync(TenantId, PoolName);

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.StringData["brokerusername"] == string.Empty &&
            s.StringData["brokerpassword"] == string.Empty));
    }

    [Test]
    public async Task CreatePoolAsync_NameComponentsAreLowercased()
    {
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, "mixedcase-mypool").Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, "mixedcase-mypool-octo-mesh-connection").Returns(false);

        await Manager.CreatePoolAsync("MixedCase", "MyPool");

        await Gateway.Received(1).CommunicationPoolExistsAsync(PoolNamespace, "mixedcase-mypool");
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreatePoolAsync_PoolNameWithWhitespace_NamesAreSanitisedAndOriginalLandsInAnnotation()
    {
        // sbeg + "Communication Pool" used to crash with apiserver 422 because
        // the secret name was "sbeg-communication pool-octo-mesh-connection"
        // (whitespace, mixed case) — invalid RFC 1123 subdomain. Verify the
        // sanitiser collapses the space to '-', lowercases, and the original
        // poolName is preserved in an annotation for UI/debugging.
        const string crName = "sbeg-communication-pool";
        const string secretName = "sbeg-communication-pool-octo-mesh-connection";
        Gateway.CommunicationPoolExistsAsync(PoolNamespace, crName).Returns(false);
        Gateway.SecretExistsAsync(PoolNamespace, secretName).Returns(false);

        await Manager.CreatePoolAsync("sbeg", "Communication Pool");

        await Gateway.Received(1).CreateSecretAsync(PoolNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == secretName &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/pool"] == "Communication-Pool" &&
            s.Metadata.Annotations["octo-mesh.meshmakers.io/pool-name"] == "Communication Pool"));
        await Gateway.Received(1).CreateCommunicationPoolAsync(PoolNamespace, Arg.Any<object>());
    }
}
