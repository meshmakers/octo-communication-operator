using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.DeploymentSiteManagerTests;

public abstract class DeploymentSiteManagerTestsBase
{
    protected const string TenantId = "acme";
    protected const string DeploymentSiteName = "default";
    protected const string DeploymentSiteRtId = "65d5c447b420da3fb12381bc";
    protected const string DeploymentSiteNamespace = "octo";
    protected const string ExpectedCrName = "acme-65d5c447b420da3fb12381bc";
    protected const string ExpectedSecretName = "acme-65d5c447b420da3fb12381bc-octo-mesh-connection";

    protected readonly IDeploymentSiteKubernetesGateway Gateway;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly DeploymentSiteManager Manager;

    protected DeploymentSiteManagerTestsBase()
    {
        Gateway = Substitute.For<IDeploymentSiteKubernetesGateway>();
        OperatorOptions = new OperatorOptions
        {
            DeploymentSiteNamespace = DeploymentSiteNamespace,
            CommunicationControllerUri = "https://controller",
            InstancePrefix = "instance",
            AdapterIgnoreCertificateValidation = false,
            BrokerHost = "rabbit",
            BrokerVirtualHost = "/",
            BrokerPort = 5672,
            BrokerUser = "octo",
            BrokerPassword = "secret"
        };

        Manager = new DeploymentSiteManager(
            NullLogger<DeploymentSiteManager>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions),
            Gateway);
    }
}
