using Meshmakers.Octo.Sdk.ServiceClient.PlugControllerServices;
using PlugOperator.Entities;

namespace PlugOperator.Models;

public class Pool
{
    public PoolDescriptor PoolDescriptor { get; }
    public IPlugPoolControllerClient PlugPoolControllerClient { get; }
    public V1PlugPoolEntity Entity { get; }

    public Pool(PoolDescriptor poolDescriptor, IPlugPoolControllerClient plugPoolControllerClient, V1PlugPoolEntity entity)
    {
        PoolDescriptor = poolDescriptor;
        PlugPoolControllerClient = plugPoolControllerClient;
        Entity = entity;
    }
}