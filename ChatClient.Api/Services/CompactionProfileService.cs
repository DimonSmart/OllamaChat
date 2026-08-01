using ChatClient.Api.Services.Seed;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class CompactionProfileService(
    ICompactionProfileRepository repository,
    IAgentTemplateRepository agentTemplateRepository,
    CompactionProfileSeeder seeder) : ICompactionProfileService
{
    public Task<IReadOnlyCollection<CompactionProfile>> GetAllAsync() => repository.GetAllAsync();

    public async Task<CompactionProfile?> GetByIdAsync(Guid id) =>
        (await repository.GetAllAsync()).FirstOrDefault(profile => profile.Id == id);

    public async Task CreateAsync(CompactionProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        NormalizeAndValidate(profile, profiles, null);
        var usedIds = profiles.Select(static profile => profile.Id).ToHashSet();
        while (profile.Id == Guid.Empty || !usedIds.Add(profile.Id))
            profile.Id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        profile.CreatedAt = now;
        profile.UpdatedAt = now;
        profiles.Add(profile);
        await repository.SaveAllAsync(profiles);
    }

    public async Task UpdateAsync(CompactionProfile profile)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var index = profiles.FindIndex(item => item.Id == profile.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Compaction profile with ID {profile.Id} not found");
        NormalizeAndValidate(profile, profiles, profile.Id);
        profile.CreatedAt = profiles[index].CreatedAt;
        profile.UpdatedAt = DateTime.UtcNow;
        profiles[index] = profile;
        await repository.SaveAllAsync(profiles);
    }

    public async Task DeleteAsync(Guid id)
    {
        var profiles = (await repository.GetAllAsync()).ToList();
        var profile = profiles.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Compaction profile with ID {id} not found");
        var users = (await agentTemplateRepository.GetAllAsync())
            .Where(agent => agent.CompactionProfileId == id)
            .Select(agent => agent.AgentName)
            .ToList();
        if (users.Count != 0)
            throw new InvalidOperationException($"Compaction profile '{profile.Name}' is used by saved agents: {string.Join(", ", users)}.");
        profiles.Remove(profile);
        await repository.SaveAllAsync(profiles);
    }

    public Task RestoreBuiltInAsync() => seeder.RestoreAsync();

    private static void NormalizeAndValidate(CompactionProfile profile, IReadOnlyCollection<CompactionProfile> profiles, Guid? excludedId)
    {
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Kind = profile.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        profile.BudgetSource = profile.BudgetSource?.Trim().ToLowerInvariant() ?? string.Empty;
        profile.Stages ??= [];
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));
        if (profiles.Any(item => item.Id != excludedId && string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A Compaction profile named '{profile.Name}' already exists.", nameof(profile));
        if (profile.Kind is not (CompactionProfileKinds.ContextWindow or CompactionProfileKinds.CustomPipeline))
            throw new ArgumentException("Compaction profile kind is invalid.", nameof(profile));
        if (profile.BudgetSource is not (CompactionBudgetSources.SelectedModel or CompactionBudgetSources.Fixed))
            throw new ArgumentException("Compaction budget source is invalid.", nameof(profile));
        if (profile.BudgetSource == CompactionBudgetSources.Fixed && (profile.ContextWindowTokens is null || profile.MaxOutputTokens is null))
            throw new ArgumentException("Fixed budgets require context-window and maximum-output limits.", nameof(profile));
        if (profile.ContextWindowTokens is int context && context <= 0 || profile.MaxOutputTokens is int output && output <= 0 || profile.ContextWindowTokens is int window && profile.MaxOutputTokens is int maxOutput && maxOutput >= window)
            throw new ArgumentException("Compaction limits must be positive and maximum output must be smaller than the context window.", nameof(profile));
        if (profile.Kind == CompactionProfileKinds.ContextWindow && profile.Stages.Count != 0)
            throw new ArgumentException("Context-window profiles cannot define custom stages.", nameof(profile));
        if (profile.Kind == CompactionProfileKinds.CustomPipeline && profile.Stages.Count == 0)
            throw new ArgumentException("Custom-pipeline profiles require at least one stage.", nameof(profile));
        foreach (var stage in profile.Stages)
            NormalizeAndValidateStage(stage);
    }

    private static void NormalizeAndValidateStage(CompactionStage stage)
    {
        stage.Kind = stage.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        stage.SummaryInstructions = NormalizeOptionalText(stage.SummaryInstructions);
        stage.SummarizerModelName = NormalizeOptionalText(stage.SummarizerModelName);
        if (stage.Kind is not (CompactionStageKinds.ToolResult or CompactionStageKinds.Truncation or CompactionStageKinds.Summarization or CompactionStageKinds.SlidingWindow))
            throw new ArgumentException("Compaction stage kind is invalid.", nameof(stage));
        if (stage.TriggerTokenCount <= 0 || stage.TargetTokenCount <= 0 || stage.TargetTokenCount >= stage.TriggerTokenCount)
            throw new ArgumentException("Compaction stage token limits must be positive and the target must be smaller than the trigger.", nameof(stage));
        if (stage.Kind == CompactionStageKinds.Summarization)
        {
            var hasSummarizerServer = stage.SummarizerLlmId is Guid summarizerLlmId && summarizerLlmId != Guid.Empty;
            var hasSummarizerModel = stage.SummarizerModelName is not null;
            if (hasSummarizerServer != hasSummarizerModel)
                throw new ArgumentException("A separately selected summarizer requires both a server ID and model name.", nameof(stage));
            if (stage.SummarizerLlmId == Guid.Empty)
                throw new ArgumentException("A summarizer model ID cannot be empty.", nameof(stage));
        }
        if (stage.Kind != CompactionStageKinds.Summarization && (stage.SummaryInstructions is not null || stage.SummarizerLlmId is not null || stage.SummarizerModelName is not null))
            throw new ArgumentException("Only summarization stages can configure a summarizer.", nameof(stage));
    }

    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
