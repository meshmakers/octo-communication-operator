using PlugOperator.Entities;

namespace PlugOperator.Services;

public interface IPlugPoolService
{
    Task RegisterPoolAsync(V1PlugPoolEntity entity);
    Task UnRegisterPoolAsync(V1PlugPoolEntity entity);
}