using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IModelRuntimeLimitsService
{
    Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync();

    Task<ModelRuntimeLimits?> GetAsync(Guid serverId, string modelName);

    Task CreateAsync(ModelRuntimeLimits limits);

    Task UpdateAsync(ModelRuntimeLimits limits);

    Task DeleteAsync(Guid serverId, string modelName);
}
