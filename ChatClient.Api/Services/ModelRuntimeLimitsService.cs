using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class ModelRuntimeLimitsService(IModelRuntimeLimitsRepository repository) : IModelRuntimeLimitsService
{
    public Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync() => repository.GetAllAsync();

    public async Task<ModelRuntimeLimits?> GetAsync(Guid serverId, string modelName) =>
        (await repository.GetAllAsync()).FirstOrDefault(item => item.ServerId == serverId &&
            string.Equals(item.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task CreateAsync(ModelRuntimeLimits limits)
    {
        var all = (await repository.GetAllAsync()).ToList();
        NormalizeAndValidate(limits, all, null);
        var now = DateTime.UtcNow;
        limits.CreatedAt = now;
        limits.UpdatedAt = now;
        all.Add(limits);
        await repository.SaveAllAsync(all);
    }

    public async Task UpdateAsync(ModelRuntimeLimits limits)
    {
        var all = (await repository.GetAllAsync()).ToList();
        var index = all.FindIndex(item => item.ServerId == limits.ServerId &&
            string.Equals(item.ModelName, limits.ModelName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new KeyNotFoundException($"Runtime limits for model '{limits.ModelName}' on server {limits.ServerId} were not found.");
        NormalizeAndValidate(limits, all, (limits.ServerId, all[index].ModelName));
        limits.CreatedAt = all[index].CreatedAt;
        limits.UpdatedAt = DateTime.UtcNow;
        all[index] = limits;
        await repository.SaveAllAsync(all);
    }

    public async Task DeleteAsync(Guid serverId, string modelName)
    {
        var all = (await repository.GetAllAsync()).ToList();
        var item = all.FirstOrDefault(item => item.ServerId == serverId && string.Equals(item.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Runtime limits for model '{modelName}' on server {serverId} were not found.");
        all.Remove(item);
        await repository.SaveAllAsync(all);
    }

    private static void NormalizeAndValidate(ModelRuntimeLimits limits, IReadOnlyCollection<ModelRuntimeLimits> all, (Guid ServerId, string ModelName)? excluded)
    {
        limits.ModelName = limits.ModelName?.Trim() ?? string.Empty;
        if (limits.ServerId == Guid.Empty)
            throw new ArgumentException("Server ID is required.", nameof(limits));
        if (string.IsNullOrWhiteSpace(limits.ModelName))
            throw new ArgumentException("Model name is required.", nameof(limits));
        if (limits.ContextWindowTokens <= 0 || limits.MaxOutputTokens <= 0 || limits.MaxOutputTokens >= limits.ContextWindowTokens)
            throw new ArgumentException("Context window and maximum output must be positive, and maximum output must be smaller than the context window.", nameof(limits));
        if (all.Any(item => (excluded is null || item.ServerId != excluded.Value.ServerId || !string.Equals(item.ModelName, excluded.Value.ModelName, StringComparison.OrdinalIgnoreCase)) &&
                            item.ServerId == limits.ServerId && string.Equals(item.ModelName, limits.ModelName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Runtime limits for model '{limits.ModelName}' already exist on this server.", nameof(limits));
    }
}
