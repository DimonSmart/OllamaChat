using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class ModelRuntimeLimitsService(IModelRuntimeLimitsRepository repository) : IModelRuntimeLimitsService
{
    private static readonly IReadOnlyDictionary<string, (int ContextWindowTokens, int? MaxOutputTokens)> KnownLimits =
        new Dictionary<string, (int, int?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["bge-large:latest"] = (512, null),
            ["bge-m3:latest"] = (8_192, null),
            ["nomic-embed-text:latest"] = (2_048, null),
            ["deepseek-r1:8b"] = (131_072, null),
            ["deepseek-r1:14b"] = (131_072, null),
            ["gemma3:4b"] = (131_072, null),
            ["gemma3:latest"] = (131_072, null),
            ["gpt-oss:latest"] = (131_072, null),
            ["granite4:3b"] = (131_072, null),
            ["llama3.2:1b"] = (131_072, null),
            ["llama3.2:3b"] = (131_072, null),
            ["llama3.2:latest"] = (131_072, null),
            ["llama3.2-vision:latest"] = (131_072, null),
            ["llava:latest"] = (32_768, null),
            ["mistral-small3.2:latest"] = (131_072, null),
            ["olmo-3:latest"] = (65_536, null),
            ["phi3:latest"] = (131_072, null),
            ["phi4-mini:3.8b"] = (131_072, null),
            ["phi4-mini:3.8b-fp16"] = (4_096, null),
            ["phi4:latest"] = (16_384, null),
            ["qwen2:7b"] = (32_768, null),
            ["qwen3:latest"] = (40_960, null),
            ["qwen3-coder:latest"] = (262_144, null),
            ["qwen3-vl:8b"] = (262_144, null),
            ["qwen3.5:latest"] = (262_144, null),
            ["deepseek-v3.1:671b-cloud"] = (163_840, null),
            ["gemini-3-pro-preview:latest"] = (1_048_576, 65_536),
            ["gemma4:31b-cloud"] = (262_144, null),
            ["gpt-oss:20b-cloud"] = (131_072, null),
            ["gpt-oss:120b-cloud"] = (131_072, null),
            ["kimi-k2-thinking:cloud"] = (262_144, null),
            ["ministral-3:14b-cloud"] = (262_144, null),
            ["qwen3-next:80b-cloud"] = (262_144, null),
            ["deepseek-v4-flash:preview"] = (1_048_576, null),
            ["deepseek-v4-flash:0731"] = (1_048_576, null),
            ["deepseek-v4-pro:preview"] = (1_048_576, null),
            ["deepseek-v4-pro:0813"] = (1_048_576, null),
            ["gemma4:31b"] = (262_144, null),
            ["glm-5.1"] = (202_752, null),
            ["glm-5.2"] = (999_424, null),
            ["gpt-oss:20b"] = (131_072, null),
            ["gpt-oss:120b"] = (131_072, null),
            ["kimi-k2.6"] = (262_144, null),
            ["kimi-k2.7-code"] = (262_144, null),
            ["kimi-k3"] = (1_048_576, null),
            ["minimax-m2.7"] = (204_800, null),
            ["minimax-m3"] = (524_288, null),
            ["mistral-large-3:675b"] = (262_144, null),
            ["nemotron-3-nano:30b"] = (1_048_576, null),
            ["nemotron-3-super"] = (262_144, null),
            ["nemotron-3-ultra"] = (262_144, null),
            ["qwen3.5:397b"] = (262_144, null),
            ["gpt-5.6-sol"] = (1_050_000, 128_000),
            ["gpt-5.6-terra"] = (1_050_000, 128_000),
            ["gpt-5.6-luna"] = (1_050_000, 128_000),
            ["gpt-5.5"] = (1_050_000, 128_000),
            ["gpt-5.4"] = (1_050_000, 128_000),
            ["gpt-5.4-pro"] = (1_050_000, 128_000),
            ["gpt-5.4-mini"] = (400_000, 128_000),
            ["gpt-5.4-nano"] = (400_000, 128_000),
            ["gpt-5.3-codex"] = (400_000, 128_000),
            ["gpt-5.3-chat"] = (128_000, 16_384),
            ["gpt-5.2"] = (400_000, 128_000),
            ["gpt-5.2-codex"] = (400_000, 128_000),
            ["gpt-5.2-chat"] = (128_000, 16_384),
            ["gpt-5.1"] = (400_000, 128_000),
            ["gpt-5.1-codex"] = (400_000, 128_000),
            ["gpt-5.1-codex-mini"] = (400_000, 128_000),
            ["gpt-5.1-codex-max"] = (400_000, 128_000),
            ["gpt-5.1-chat"] = (128_000, 16_384),
            ["gpt-5"] = (400_000, 128_000),
            ["gpt-5-mini"] = (400_000, 128_000),
            ["gpt-5-nano"] = (400_000, 128_000),
            ["gpt-5-pro"] = (400_000, 128_000),
            ["gpt-5-codex"] = (400_000, 128_000),
            ["gpt-5-chat"] = (128_000, 16_384),
            ["gpt-4.1"] = (1_047_576, 32_768),
            ["gpt-4.1-mini"] = (1_047_576, 32_768),
            ["gpt-4.1-nano"] = (1_047_576, 32_768),
            ["o1"] = (200_000, 100_000),
            ["o1-preview"] = (128_000, 32_768),
            ["o1-mini"] = (128_000, 65_536),
            ["o3"] = (200_000, 100_000),
            ["o3-pro"] = (200_000, 100_000),
            ["o3-mini"] = (200_000, 100_000),
            ["o4-mini"] = (200_000, 100_000),
            ["codex-mini"] = (200_000, 100_000),
            ["gpt-4o"] = (128_000, 16_384),
            ["gpt-4o-mini"] = (128_000, 16_384),
            ["gpt-4-turbo"] = (128_000, 4_096),
            ["computer-use-preview"] = (8_192, 1_024),
            ["gpt-oss-20b"] = (131_072, 131_072),
            ["gpt-oss-120b"] = (131_072, 131_072),
            ["text-embedding-3-small"] = (8_192, null),
            ["text-embedding-3-large"] = (8_192, null),
            ["text-embedding-ada-002"] = (8_192, null),
            ["gpt-4o-audio-preview"] = (128_000, 16_384),
            ["gpt-4o-mini-audio-preview"] = (128_000, 16_384),
            ["gpt-audio"] = (128_000, 16_384),
            ["gpt-audio-mini"] = (128_000, 16_384),
            ["gpt-audio-1.5"] = (128_000, 16_384),
            ["gpt-realtime"] = (32_000, 4_096),
            ["gpt-realtime-mini"] = (32_000, 4_096),
            ["gpt-realtime-1.5"] = (32_000, 4_096),
            ["gpt-realtime-2"] = (32_000, 4_096),
            ["gpt-realtime-2.1"] = (32_000, 4_096),
            ["gpt-realtime-2.1-mini"] = (32_000, 4_096),
            ["gpt-realtime-translate"] = (32_000, 4_096),
            ["gpt-live-transcribe"] = (32_000, 4_096),
            ["gpt-realtime-whisper"] = (32_000, 4_096),
        };

    public Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync() => repository.GetAllAsync();

    public async Task<ModelRuntimeLimits?> GetAsync(Guid serverId, string modelName) =>
        (await repository.GetAllAsync()).FirstOrDefault(item => item.ServerId == serverId &&
            string.Equals(item.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<ModelRuntimeLimitsFillResult> FillKnownAsync(
        IEnumerable<ServerModel> models,
        int defaultContextWindowTokens = ModelRuntimeLimitsDefaults.DefaultContextWindowTokens)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (defaultContextWindowTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultContextWindowTokens), "Default context window must be positive.");

        var all = (await repository.GetAllAsync()).ToList();
        var added = 0;
        var alreadyConfigured = 0;
        var fallback = 0;
        var now = DateTime.UtcNow;

        foreach (var model in models
                     .Where(model => model.ServerId != Guid.Empty && !string.IsNullOrWhiteSpace(model.ModelName))
                     .DistinctBy(model => (model.ServerId, model.ModelName.Trim()), ServerModelComparer.Instance))
        {
            var modelName = model.ModelName.Trim();
            if (all.Any(item => item.ServerId == model.ServerId &&
                                string.Equals(item.ModelName, modelName, StringComparison.OrdinalIgnoreCase)))
            {
                alreadyConfigured++;
                continue;
            }

            var isKnown = KnownLimits.TryGetValue(modelName, out var limits);
            all.Add(new ModelRuntimeLimits
            {
                ServerId = model.ServerId,
                ModelName = modelName,
                ContextWindowTokens = isKnown ? limits.ContextWindowTokens : defaultContextWindowTokens,
                MaxOutputTokens = isKnown ? limits.MaxOutputTokens : null,
                CreatedAt = now,
                UpdatedAt = now
            });
            added++;
            if (!isKnown)
                fallback++;
        }

        if (added > 0)
            await repository.SaveAllAsync(all);

        return new ModelRuntimeLimitsFillResult(added, alreadyConfigured, fallback);
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

    private static void NormalizeAndValidate(
        ModelRuntimeLimits limits,
        IReadOnlyCollection<ModelRuntimeLimits> all,
        (Guid ServerId, string ModelName)? excluded)
    {
        limits.ModelName = limits.ModelName?.Trim() ?? string.Empty;
        if (limits.ServerId == Guid.Empty)
            throw new ArgumentException("Server ID is required.", nameof(limits));
        if (string.IsNullOrWhiteSpace(limits.ModelName))
            throw new ArgumentException("Model name is required.", nameof(limits));
        if (!ModelRuntimeLimitValidation.HasValidRuntimeLimits(limits.ContextWindowTokens, limits.MaxOutputTokens))
            throw new ArgumentException("Context window must be positive. Maximum output, when set, must be positive and no greater than the context window.", nameof(limits));
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
