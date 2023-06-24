using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Communication.Operator.Models;

public class Pool
{
    public PoolDescriptor PoolDescriptor { get; }
    public IPoolHubClient PoolHubClient { get; }
    public V1CommunicationPoolEntity Entity { get; }
    
    public bool IsRegistered { get; set; }

    public Pool(PoolDescriptor poolDescriptor, IPoolHubClient poolHubClient, V1CommunicationPoolEntity entity)
    {
        PoolDescriptor = poolDescriptor;
        PoolHubClient = poolHubClient;
        Entity = entity;
    }
}