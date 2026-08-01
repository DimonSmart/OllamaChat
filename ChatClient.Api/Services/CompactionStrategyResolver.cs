using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
#pragma warning disable MAAI001
using Microsoft.Agents.AI.Compaction;

namespace ChatClient.Api.Services;

public sealed record ResolvedCompactionStrategy(
    CompactionStrategy Strategy,
    CompactionBudget Budget,
    IReadOnlyList<string> StageKinds);

public interface ICompactionStrategyResolver
{
    Task<ResolvedCompactionStrategy?> ResolveAsync(
        CompactionProfile? profile,
        ServerModel primaryModel,
        IChatClient primaryChatClient,
        CancellationToken cancellationToken = default);
}

public sealed class CompactionStrategyResolver(
    ICompactionBudgetResolver budgetResolver,
    ILlmChatClientFactory chatClientFactory) : ICompactionStrategyResolver
{
    public async Task<ResolvedCompactionStrategy?> ResolveAsync(
        CompactionProfile? profile,
        ServerModel primaryModel,
        IChatClient primaryChatClient,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
            return null;
        ArgumentNullException.ThrowIfNull(primaryModel);
        ArgumentNullException.ThrowIfNull(primaryChatClient);
        var budget = await budgetResolver.ResolveAsync(profile, primaryModel);

        if (profile.Kind == CompactionProfileKinds.ContextWindow)
        {
            return new ResolvedCompactionStrategy(
                new ContextWindowCompactionStrategy(budget.ContextWindowTokens, budget.MaxOutputTokens, 0.5, 0.8),
                budget,
                []);
        }

        if (profile.Kind != CompactionProfileKinds.CustomPipeline || profile.Stages.Count == 0)
            throw new InvalidOperationException($"Compaction profile '{profile.Name}' has an invalid strategy configuration.");

        foreach (var stage in profile.Stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStage(stage, profile.Name);
        }

        var strategies = new List<CompactionStrategy>();
        var kinds = new List<string>();
        foreach (var stage in profile.Stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trigger = CompactionTriggers.TokensExceed(stage.TriggerTokenCount);
            var target = CompactionTriggers.TokensBelow(stage.TargetTokenCount);
            CompactionStrategy strategy = stage.Kind switch
            {
                CompactionStageKinds.ToolResult => new ToolResultCompactionStrategy(trigger, 1, target),
                CompactionStageKinds.Truncation => new TruncationCompactionStrategy(trigger, 1, target),
                CompactionStageKinds.SlidingWindow => new SlidingWindowCompactionStrategy(trigger, 1, target),
                CompactionStageKinds.Summarization => new SummarizationCompactionStrategy(
                    await ResolveSummarizerClientAsync(stage, primaryChatClient, cancellationToken), trigger, 1, stage.SummaryInstructions, target),
                _ => throw new InvalidOperationException($"Compaction profile '{profile.Name}' has unknown stage '{stage.Kind}'.")
            };
            strategies.Add(strategy);
            kinds.Add(stage.Kind);
        }

        return new ResolvedCompactionStrategy(new PipelineCompactionStrategy(strategies), budget, kinds);
    }

    private async Task<IChatClient> ResolveSummarizerClientAsync(CompactionStage stage, IChatClient primaryChatClient, CancellationToken cancellationToken)
    {
        if (stage.SummarizerLlmId is null && stage.SummarizerModelName is null)
            return primaryChatClient;
        if (stage.SummarizerLlmId is not Guid serverId || serverId == Guid.Empty || string.IsNullOrWhiteSpace(stage.SummarizerModelName))
            throw new InvalidOperationException("A summarization stage requires both a valid server ID and model name for a separate summarizer.");
        return await chatClientFactory.CreateAsync(new ServerModel(serverId, stage.SummarizerModelName), cancellationToken);
    }

    private static void ValidateStage(CompactionStage stage, string profileName)
    {
        if (stage.Kind is not (CompactionStageKinds.ToolResult or
            CompactionStageKinds.Truncation or
            CompactionStageKinds.SlidingWindow or
            CompactionStageKinds.Summarization))
        {
            throw new InvalidOperationException($"Compaction profile '{profileName}' has unknown stage '{stage.Kind}'.");
        }

        if (stage.TriggerTokenCount <= 0 || stage.TargetTokenCount <= 0 || stage.TargetTokenCount >= stage.TriggerTokenCount)
            throw new InvalidOperationException($"Compaction profile '{profileName}' has invalid token targets for stage '{stage.Kind}'.");

        if (stage.Kind == CompactionStageKinds.Summarization &&
            ((stage.SummarizerLlmId is null) != (stage.SummarizerModelName is null) ||
             stage.SummarizerLlmId == Guid.Empty ||
             (stage.SummarizerModelName is not null && string.IsNullOrWhiteSpace(stage.SummarizerModelName))))
        {
            throw new InvalidOperationException("A summarization stage requires both a valid server ID and model name for a separate summarizer.");
        }
    }
}
#pragma warning restore MAAI001
