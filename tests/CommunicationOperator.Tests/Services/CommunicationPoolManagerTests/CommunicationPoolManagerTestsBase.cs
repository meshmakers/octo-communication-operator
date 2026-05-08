using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services.CommunicationPoolManagerTests;

public abstract class CommunicationPoolManagerTestsBase
{
    protected const string TenantId = "acme";
    protected const string PoolNamespace = "octo";
    protected const string DefaultPoolName = "default";
    protected const string ExpectedCrName = "acme-default";
    protected const string ExpectedSecretName = "acme-default-octo-mesh-connection";

    protected readonly ICommunicationPoolKubernetesGateway Gateway;
    protected readonly OperatorOptions OperatorOptions;
    protected readonly CommunicationPoolManager Manager;

    protected CommunicationPoolManagerTestsBase()
    {
        Gateway = Substitute.For<ICommunicationPoolKubernetesGateway>();
        OperatorOptions = new OperatorOptions
        {
            PoolNamespace = PoolNamespace,
            DefaultPoolName = DefaultPoolName,
            CommunicationControllerUri = "https://controller",
            InstancePrefix = "instance",
            AdapterIgnoreCertificateValidation = false,
            BrokerHost = "rabbit",
            BrokerVirtualHost = "/",
            BrokerPort = 5672,
            BrokerUser = "octo",
            BrokerPassword = "secret"
        };

        Manager = new CommunicationPoolManager(
            NullLogger<CommunicationPoolManager>.Instance,
            Microsoft.Extensions.Options.Options.Create(OperatorOptions),
            Gateway);
    }
}
