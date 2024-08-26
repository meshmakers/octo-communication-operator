using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Models;

public class Pool(PoolDescriptor poolDescriptor, IPoolHubClient poolHubClient, V1CommunicationPoolEntity entity)
{
    public PoolDescriptor PoolDescriptor { get; } = poolDescriptor;
    public IPoolHubClient PoolHubClient { get; } = poolHubClient;
    public V1CommunicationPoolEntity Entity { get; } = entity;

    public bool IsRegistered { get; set; }
}