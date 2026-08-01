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
    Task PreflightAsync(
        CompactionProfile profile,
        CancellationToken cancellationToken = default);

    Task<ResolvedCompactionStrategy?> ResolveAsync(
        CompactionProfile? profile,
        ServerModel primaryModel,
        IChatClient primaryChatClient,
        CancellationToken cancellationToken = default);
}

public sealed class CompactionStrategyResolver(
    ICompactionBudgetResolver budgetResolver,
    ILlmChatClientFactory chatClientFactory,
    IModelCapabilityService modelCapabilityService) : ICompactionStrategyResolver
{
    public async Task PreflightAsync(CompactionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Kind != CompactionProfileKinds.CustomPipeline)
            return;

        for (var index = 0; index < profile.Stages.Count; index++)
        {
            var stage = profile.Stages[index];
            if (stage.Kind != CompactionStageKinds.Summarization ||
                (stage.SummarizerLlmId is null && stage.SummarizerModelName is null))
                continue;

            if (stage.SummarizerLlmId is not Guid serverId || serverId == Guid.Empty ||
                string.IsNullOrWhiteSpace(stage.SummarizerModelName))
                throw new InvalidOperationException($"Compaction profile '{profile.Name}', summarization stage {index + 1} requires both a valid server ID and model name for a separate summarizer.");

            var model = new ServerModel(serverId, stage.SummarizerModelName.Trim());
            try
            {
                await modelCapabilityService.EnsureModelSupportedByServerAsync(model, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Compaction profile '{profile.Name}', summarization stage {index + 1}: separate summarizer '{model.ModelName}' on server '{model.ServerId}' is unavailable. {exception.Message}",
                    exception);
            }
        }
    }

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
            ValidateContextWindowThresholds(profile);
            return new ResolvedCompactionStrategy(
                new ContextWindowCompactionStrategy(
                    budget.ContextWindowTokens,
                    budget.MaxOutputTokens,
                    profile.ToolResultThreshold,
                    profile.TruncationThreshold),
                budget,
                [CompactionStageKinds.ToolResult, CompactionStageKinds.Truncation]);
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
            var trigger = CreateTrigger(stage.Trigger, budget, isTarget: false);
            var target = CreateTrigger(stage.Target, budget, isTarget: true);
            CompactionStrategy strategy = stage.Kind switch
            {
                CompactionStageKinds.ToolResult => new ToolResultCompactionStrategy(trigger, stage.MinimumPreservedGroups, target),
                CompactionStageKinds.Truncation => new TruncationCompactionStrategy(trigger, stage.MinimumPreservedGroups, target),
                CompactionStageKinds.SlidingWindow => new SlidingWindowCompactionStrategy(trigger, stage.MinimumPreservedTurns, target),
                CompactionStageKinds.Summarization => new SummarizationCompactionStrategy(
                    await ResolveSummarizerClientAsync(stage, primaryChatClient, cancellationToken), trigger, stage.MinimumPreservedGroups, stage.SummaryInstructions, target),
                _ => throw new InvalidOperationException($"Compaction profile '{profile.Name}' has unknown stage '{stage.Kind}'.")
            };
            strategies.Add(strategy);
            kinds.Add(stage.Kind);
        }

        return new ResolvedCompactionStrategy(new PipelineCompactionStrategy(strategies), budget, kinds);
    }

    private static CompactionTrigger CreateTrigger(CompactionLimit limit, CompactionBudget budget, bool isTarget)
    {
        var value = limit.Kind == CompactionLimitKinds.InputBudgetPercent
            ? checked((int)Math.Floor(budget.InputBudgetTokens * limit.Value))
            : checked((int)limit.Value);

        return (limit.Kind, isTarget) switch
        {
            (CompactionLimitKinds.InputBudgetPercent or CompactionLimitKinds.Tokens, false) => CompactionTriggers.TokensExceed(value),
            (CompactionLimitKinds.InputBudgetPercent or CompactionLimitKinds.Tokens, true) => CompactionTriggers.TokensBelow(value),
            (CompactionLimitKinds.Messages, false) => CompactionTriggers.MessagesExceed(value),
            (CompactionLimitKinds.Messages, true) => index => index.IncludedMessageCount <= value,
            (CompactionLimitKinds.Turns, false) => CompactionTriggers.TurnsExceed(value),
            (CompactionLimitKinds.Turns, true) => index => index.IncludedTurnCount <= value,
            (CompactionLimitKinds.Groups, false) => CompactionTriggers.GroupsExceed(value),
            (CompactionLimitKinds.Groups, true) => index => index.IncludedGroupCount <= value,
            _ => throw new InvalidOperationException($"Unsupported compaction limit kind '{limit.Kind}'.")
        };
    }

    private static void ValidateContextWindowThresholds(CompactionProfile profile)
    {
        if (!(profile.ToolResultThreshold is > 0 and <= 1) ||
            !(profile.TruncationThreshold is > 0 and <= 1) ||
            profile.TruncationThreshold < profile.ToolResultThreshold)
        {
            throw new InvalidOperationException($"Compaction profile '{profile.Name}' has invalid context-window thresholds. Tool-result compaction must occur after zero and no later than history truncation, which must be at most one.");
        }
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

        if (stage.Trigger is null || stage.Target is null ||
            stage.Trigger.Kind != stage.Target.Kind || !IsSupportedLimitKind(stage.Kind, stage.Trigger.Kind) ||
            !HasValidLimitValue(stage.Trigger) || !HasValidLimitValue(stage.Target) ||
            stage.Target.Value >= stage.Trigger.Value || stage.MinimumPreservedGroups < 0 || stage.MinimumPreservedTurns < 0)
            throw new InvalidOperationException($"Compaction profile '{profileName}' has invalid limits for stage '{stage.Kind}'.");

        if (stage.Kind == CompactionStageKinds.Summarization &&
            ((stage.SummarizerLlmId is null) != (stage.SummarizerModelName is null) ||
             stage.SummarizerLlmId == Guid.Empty ||
             (stage.SummarizerModelName is not null && string.IsNullOrWhiteSpace(stage.SummarizerModelName))))
        {
            throw new InvalidOperationException("A summarization stage requires both a valid server ID and model name for a separate summarizer.");
        }

        if ((stage.Kind == CompactionStageKinds.SlidingWindow && stage.MinimumPreservedGroups != 0) ||
            (stage.Kind != CompactionStageKinds.SlidingWindow && stage.MinimumPreservedTurns != 0))
            throw new InvalidOperationException($"Compaction profile '{profileName}' has invalid preserved history settings for stage '{stage.Kind}'.");
    }

    private static bool IsSupportedLimitKind(string stageKind, string limitKind) => stageKind switch
    {
        CompactionStageKinds.SlidingWindow => limitKind == CompactionLimitKinds.Turns,
        CompactionStageKinds.ToolResult or CompactionStageKinds.Truncation or CompactionStageKinds.Summarization =>
            limitKind is CompactionLimitKinds.InputBudgetPercent or CompactionLimitKinds.Tokens or CompactionLimitKinds.Messages or CompactionLimitKinds.Groups,
        _ => false
    };

    private static bool HasValidLimitValue(CompactionLimit limit) =>
        double.IsFinite(limit.Value) &&
        (limit.Kind == CompactionLimitKinds.InputBudgetPercent ? limit.Value is > 0 and <= 1 : limit.Value >= 0) &&
        (limit.Kind == CompactionLimitKinds.InputBudgetPercent || limit.Value == Math.Truncate(limit.Value));
}
#pragma warning restore MAAI001
