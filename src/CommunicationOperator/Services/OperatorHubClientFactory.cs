using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Services;

public class OperatorHubClientFactory : IOperatorHubClientFactory
{
    private readonly IServiceClientAccessToken _accessToken;
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="loggerFactory">Logger factory for the created client.</param>
    /// <param name="accessToken">
    ///     The operator's own service credential, kept current by
    ///     <see cref="OperatorAccessTokenService" /> (AB#5062). It is injected rather than
    ///     constructed here on purpose: the SDK reads this exact instance on every (re)connect, so a
    ///     fresh <c>ServiceClientAccessToken</c> per client - what this factory used to do - is a
    ///     permanently empty one that no refresh can ever reach.
    /// </param>
    public OperatorHubClientFactory(ILoggerFactory loggerFactory, IServiceClientAccessToken accessToken)
    {
        _loggerFactory = loggerFactory;
        _accessToken = accessToken;
    }

    public IOperatorHubClient Create(OperatorHubClientOptions options, IOperatorHubCallbacks callbacks) =>
        new OperatorHubClient(
            options,
            _loggerFactory.CreateLogger<OperatorHubClient>(),
            _accessToken,
            callbacks);
}
