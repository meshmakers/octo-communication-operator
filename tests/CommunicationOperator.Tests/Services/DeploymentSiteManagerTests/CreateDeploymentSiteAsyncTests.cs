using k8s.Models;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.DeploymentSiteManagerTests;

public class CreateDeploymentSiteAsyncTests : DeploymentSiteManagerTestsBase
{
    [Test]
    public async Task CreateDeploymentSiteAsync_CrAlreadyExists_NoCallsToCreate()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(true);

        await Manager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.DidNotReceive().CreateDeploymentSiteAsync(
            Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateDeploymentSiteAsync_CrAndSecretMissing_BothCreated()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.Received(1).CreateSecretAsync(DeploymentSiteNamespace, Arg.Any<V1Secret>());
        await Gateway.Received(1).CreateDeploymentSiteAsync(DeploymentSiteNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreateDeploymentSiteAsync_SecretExists_OnlyCrCreated()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(true);

        await Manager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.DidNotReceive().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateDeploymentSiteAsync(DeploymentSiteNamespace, Arg.Any<object>());
    }

    [Test]
    public async Task CreateDeploymentSiteAsync_BrokerSecretCarriesCredentialsAndLabels()
    {
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.Received(1).CreateSecretAsync(DeploymentSiteNamespace, Arg.Is<V1Secret>(s =>
            s.Metadata.Name == ExpectedSecretName &&
            s.Metadata.NamespaceProperty == DeploymentSiteNamespace &&
            s.Type == "Opaque" &&
            s.StringData["brokerusername"] == "octo" &&
            s.StringData["brokerpassword"] == "secret" &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/deployment-site-rt-id"] == DeploymentSiteRtId &&
            s.Metadata.Labels["octo-mesh.meshmakers.io/managed-by"] == "communication-operator"));
    }

    [Test]
    public async Task CreateDeploymentSiteAsync_NullBrokerCredentials_StoredAsEmptyStrings()
    {
        OperatorOptions.BrokerUser = null;
        OperatorOptions.BrokerPassword = null;
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, ExpectedCrName).Returns(false);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, ExpectedSecretName).Returns(false);

        await Manager.CreateDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await Gateway.Received(1).CreateSecretAsync(DeploymentSiteNamespace, Arg.Is<V1Secret>(s =>
            s.StringData["brokerusername"] == string.Empty &&
            s.StringData["brokerpassword"] == string.Empty));
    }

    [Test]
    public async Task CreateDeploymentSiteAsync_TenantIdIsLowercased()
    {
        // DeploymentSiteRtId is already DNS-safe (24-char hex). TenantId can still
        // arrive with mixed case from CK; the sanitiser must lowercase it
        // so the resulting CR/Secret name is RFC 1123.
        const string deploymentSiteRtId = "65d5c447b420da3fb12381bc";
        const string expectedCr = "mixedcase-65d5c447b420da3fb12381bc";
        const string expectedSecret = "mixedcase-65d5c447b420da3fb12381bc-octo-mesh-connection";
        Gateway.DeploymentSiteExistsAsync(DeploymentSiteNamespace, expectedCr).Returns(false);
        Gateway.SecretExistsAsync(DeploymentSiteNamespace, expectedSecret).Returns(false);

        await Manager.CreateDeploymentSiteAsync("MixedCase", deploymentSiteRtId);

        await Gateway.Received(1).DeploymentSiteExistsAsync(DeploymentSiteNamespace, expectedCr);
        await Gateway.Received(1).CreateDeploymentSiteAsync(DeploymentSiteNamespace, Arg.Any<object>());
    }
}
