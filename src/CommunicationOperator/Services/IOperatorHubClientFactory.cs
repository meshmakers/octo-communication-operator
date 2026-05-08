using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
/// Creates <see cref="IOperatorHubClient"/> instances for the
/// <see cref="OperatorHubService"/>. Exists so that the SignalR client can be
/// substituted in unit tests; production wires <see cref="OperatorHubClientFactory"/>.
/// </summary>
public interface IOperatorHubClientFactory
{
    IOperatorHubClient Create(OperatorHubClientOptions options, IOperatorHubCallbacks callbacks);
}
