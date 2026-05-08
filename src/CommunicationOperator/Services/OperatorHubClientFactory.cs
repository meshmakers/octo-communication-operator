using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class OperatorHubClientFactory : IOperatorHubClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public OperatorHubClientFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IOperatorHubClient Create(OperatorHubClientOptions options, IOperatorHubCallbacks callbacks) =>
        new OperatorHubClient(
            options,
            _loggerFactory.CreateLogger<OperatorHubClient>(),
            new ServiceClientAccessToken(),
            callbacks);
}
