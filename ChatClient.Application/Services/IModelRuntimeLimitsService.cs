using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IModelRuntimeLimitsService
{
    Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync();

    Task<ModelRuntimeLimits?> GetAsync(Guid serverId, string modelName);

    Task<ModelRuntimeLimitsFillResult> FillKnownAsync(
        IEnumerable<ServerModel> models,
        int defaultContextWindowTokens = ModelRuntimeLimitsDefaults.DefaultContextWindowTokens);

    Task CreateAsync(ModelRuntimeLimits limits);

    Task UpdateAsync(ModelRuntimeLimits limits);

    Task DeleteAsync(Guid serverId, string modelName);
}
