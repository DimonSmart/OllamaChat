using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class ModelRuntimeLimitsService(IModelRuntimeLimitsRepository repository) : IModelRuntimeLimitsService
{
    private static readonly IReadOnlyDictionary<string, (int ContextWindowTokens, int MaxOutputTokens)> KnownLimits =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4"] = (1_050_000, 128_000),
            ["gpt-5.4-pro"] = (1_050_000, 128_000),
            ["gpt-5.4-mini"] = (400_000, 128_000),
            ["gpt-5.4-nano"] = (400_000, 128_000),
            ["gpt-5"] = (400_000, 128_000),
            ["gpt-5-mini"] = (400_000, 128_000),
            ["gpt-5-nano"] = (400_000, 128_000),
            ["gpt-4.1"] = (1_047_576, 32_768),
            ["gpt-4.1-mini"] = (1_047_576, 32_768),
            ["gpt-4.1-nano"] = (1_047_576, 32_768),
            ["gpt-4o"] = (128_000, 16_384),
            ["gpt-4o-mini"] = (128_000, 16_384),
            ["o3"] = (200_000, 100_000),
            ["o4-mini"] = (200_000, 100_000)
        };

    public Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync() => repository.GetAllAsync();

    public async Task<ModelRuntimeLimits?> GetAsync(Guid serverId, string modelName) =>
        (await repository.GetAllAsync()).FirstOrDefault(item => item.ServerId == serverId &&
            string.Equals(item.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<ModelRuntimeLimitsFillResult> FillKnownAsync(IEnumerable<ServerModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var all = (await repository.GetAllAsync()).ToList();
        var added = 0;
        var alreadyConfigured = 0;
        var unknown = 0;
        var now = DateTime.UtcNow;

        foreach (var model in models
                     .Where(model => model.ServerId != Guid.Empty && !string.IsNullOrWhiteSpace(model.ModelName))
                     .DistinctBy(model => (model.ServerId, model.ModelName.Trim()), ServerModelComparer.Instance))
        {
            if (all.Any(item => item.ServerId == model.ServerId && string.Equals(item.ModelName, model.ModelName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                alreadyConfigured++;
                continue;
            }

            if (!KnownLimits.TryGetValue(model.ModelName.Trim(), out var limits))
            {
                unknown++;
                continue;
            }

            all.Add(new ModelRuntimeLimits
            {
                ServerId = model.ServerId,
                ModelName = model.ModelName.Trim(),
                ContextWindowTokens = limits.ContextWindowTokens,
                MaxOutputTokens = limits.MaxOutputTokens,
                CreatedAt = now,
                UpdatedAt = now
            });
            added++;
        }

        if (added > 0)
            await repository.SaveAllAsync(all);

        return new ModelRuntimeLimitsFillResult(added, alreadyConfigured, unknown);
    }

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
        if (!ModelRuntimeLimitValidation.HasValidTokenBudget(limits.ContextWindowTokens, limits.MaxOutputTokens))
            throw new ArgumentException("Context window and maximum output must be positive, and maximum output must be smaller than the context window.", nameof(limits));
        if (all.Any(item => (excluded is null || item.ServerId != excluded.Value.ServerId || !string.Equals(item.ModelName, excluded.Value.ModelName, StringComparison.OrdinalIgnoreCase)) &&
                            item.ServerId == limits.ServerId && string.Equals(item.ModelName, limits.ModelName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Runtime limits for model '{limits.ModelName}' already exist on this server.", nameof(limits));
    }

    private sealed class ServerModelComparer : IEqualityComparer<(Guid ServerId, string ModelName)>
    {
        public static readonly ServerModelComparer Instance = new();

        public bool Equals((Guid ServerId, string ModelName) x, (Guid ServerId, string ModelName) y) =>
            x.ServerId == y.ServerId && string.Equals(x.ModelName, y.ModelName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid ServerId, string ModelName) value) =>
            HashCode.Combine(value.ServerId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.ModelName));
    }
}
