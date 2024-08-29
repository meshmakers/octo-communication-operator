using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Services;

public interface IPoolService
{
    Task RegisterPoolAsync(V1CommunicationPoolEntity entity, CancellationToken cancellationToken);
    Task UnRegisterPoolAsync(V1CommunicationPoolEntity entity);
}