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

internal readonly record struct ResolvedCompactionLimit(string Kind, int Value);

internal sealed record ResolvedStageLimits(
    CompactionTrigger Trigger,
    CompactionTrigger Target,
    int TriggerValue,
    int TargetValue);

public interface ICompactionStrategyResolver
{
    Task PreflightAsync(
        CompactionProfile profile,
        CancellationToken cancellationToken = default);

    Task<ResolvedCompactionStrategy?> ResolveAsync(
        CompactionProfile? profile,
        ServerModel primaryModel,
        IChatClient primaryChatClient,
        Func<ServerModel, CancellationToken, Task<IChatClient>> createOwnedChatClient,
        CancellationToken cancellationToken = default);
}

public sealed class CompactionStrategyResolver(
    ICompactionBudgetResolver budgetResolver,
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
        Func<ServerModel, CancellationToken, Task<IChatClient>> createOwnedChatClient,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
            return null;
        ArgumentNullException.ThrowIfNull(primaryModel);
        ArgumentNullException.ThrowIfNull(primaryChatClient);
        ArgumentNullException.ThrowIfNull(createOwnedChatClient);
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

        var resolvedLimits = new List<ResolvedStageLimits>(profile.Stages.Count);
        for (var index = 0; index < profile.Stages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = profile.Stages[index];
            resolvedLimits.Add(ResolveStageLimits(stage, budget, profile.Name, index + 1));
        }

        var strategies = new List<CompactionStrategy>();
        var kinds = new List<string>();
        var summarizers = new Dictionary<ServerModel, IChatClient>();
        for (var index = 0; index < profile.Stages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = profile.Stages[index];
            var limits = resolvedLimits[index];
            CompactionStrategy strategy = stage.Kind switch
            {
                CompactionStageKinds.ToolResult => new ToolResultCompactionStrategy(limits.Trigger, stage.MinimumPreservedGroups, limits.Target),
                CompactionStageKinds.Truncation => new TruncationCompactionStrategy(limits.Trigger, stage.MinimumPreservedGroups, limits.Target),
                CompactionStageKinds.SlidingWindow => new SlidingWindowCompactionStrategy(limits.Trigger, stage.MinimumPreservedTurns, limits.Target),
                CompactionStageKinds.Summarization => new SummarizationCompactionStrategy(
                    await ResolveSummarizerClientAsync(stage, primaryChatClient, summarizers, createOwnedChatClient, cancellationToken), limits.Trigger, stage.MinimumPreservedGroups, stage.SummaryInstructions, limits.Target),
                _ => throw new InvalidOperationException($"Compaction profile '{profile.Name}' has unknown stage '{stage.Kind}'.")
            };
            strategies.Add(strategy);
            kinds.Add(stage.Kind);
        }

        return new ResolvedCompactionStrategy(new PipelineCompactionStrategy(strategies), budget, kinds);
    }

    private static ResolvedStageLimits ResolveStageLimits(CompactionStage stage, CompactionBudget budget, string profileName, int stageIndex)
    {
        var trigger = ResolveLimit(stage.Trigger, budget, profileName, stageIndex, stage.Kind, "trigger");
        var target = ResolveLimit(stage.Target, budget, profileName, stageIndex, stage.Kind, "target");
        var tokenBased = trigger.Kind is CompactionLimitKinds.Tokens or CompactionLimitKinds.InputBudgetPercent;
        var valid = tokenBased
            ? trigger.Value > 0 && target.Value > 0 && target.Value < trigger.Value
            : trigger.Value > 0 && target.Value >= 0 && target.Value < trigger.Value;
        if (!valid)
            throw CreateResolvedLimitException(stage, budget, profileName, stageIndex, trigger, target);

        return new ResolvedStageLimits(
            CreateTrigger(trigger.Kind, trigger.Value, false),
            CreateTrigger(target.Kind, target.Value, true),
            trigger.Value,
            target.Value);
    }

    private static ResolvedCompactionLimit ResolveLimit(CompactionLimit definition, CompactionBudget budget, string profileName, int stageIndex, string stageKind, string role)
    {
        try
        {
            var value = definition.Kind == CompactionLimitKinds.InputBudgetPercent
                ? checked((int)Math.Floor(budget.InputBudgetTokens * definition.Value))
                : checked((int)definition.Value);
            return new ResolvedCompactionLimit(definition.Kind, value);
        }
        catch (Exception exception) when (exception is OverflowException)
        {
            throw new InvalidOperationException($"Compaction profile '{profileName}', stage {stageIndex} '{stageKind}' has an invalid {role} limit.", exception);
        }
    }

    private static InvalidOperationException CreateResolvedLimitException(CompactionStage stage, CompactionBudget budget, string profileName, int stageIndex, ResolvedCompactionLimit trigger, ResolvedCompactionLimit target) =>
        new($"Compaction profile '{profileName}', stage {stageIndex} '{stage.Kind}' with {trigger.Kind} limits resolves trigger {stage.Trigger.Value} to {trigger.Value} and target {stage.Target.Value} to {target.Value} (input budget {budget.InputBudgetTokens}). The target must be lower than a positive trigger{(trigger.Kind is CompactionLimitKinds.Tokens or CompactionLimitKinds.InputBudgetPercent ? " and both token thresholds must be positive" : string.Empty)}.");

    private static CompactionTrigger CreateTrigger(string kind, int value, bool isTarget) =>
        (kind, isTarget) switch
        {
            (CompactionLimitKinds.InputBudgetPercent or CompactionLimitKinds.Tokens, false) => CompactionTriggers.TokensExceed(value),
            (CompactionLimitKinds.InputBudgetPercent or CompactionLimitKinds.Tokens, true) => CompactionTriggers.TokensBelow(value),
            (CompactionLimitKinds.Messages, false) => CompactionTriggers.MessagesExceed(value),
            (CompactionLimitKinds.Messages, true) => index => index.IncludedMessageCount <= value,
            (CompactionLimitKinds.Turns, false) => CompactionTriggers.TurnsExceed(value),
            (CompactionLimitKinds.Turns, true) => index => index.IncludedTurnCount <= value,
            (CompactionLimitKinds.Groups, false) => CompactionTriggers.GroupsExceed(value),
            (CompactionLimitKinds.Groups, true) => index => index.IncludedGroupCount <= value,
            _ => throw new InvalidOperationException($"Unsupported compaction limit kind '{kind}'.")
        };

    private static void ValidateContextWindowThresholds(CompactionProfile profile)
    {
        if (!(profile.ToolResultThreshold is > 0 and <= 1) ||
            !(profile.TruncationThreshold is > 0 and <= 1) ||
            profile.TruncationThreshold < profile.ToolResultThreshold)
        {
            throw new InvalidOperationException($"Compaction profile '{profile.Name}' has invalid context-window thresholds. Tool-result compaction must occur after zero and no later than history truncation, which must be at most one.");
        }
    }

    private static async Task<IChatClient> ResolveSummarizerClientAsync(CompactionStage stage, IChatClient primaryChatClient, Dictionary<ServerModel, IChatClient> summarizers, Func<ServerModel, CancellationToken, Task<IChatClient>> createOwnedChatClient, CancellationToken cancellationToken)
    {
        if (stage.SummarizerLlmId is null && stage.SummarizerModelName is null)
            return primaryChatClient;
        if (stage.SummarizerLlmId is not Guid serverId || serverId == Guid.Empty || string.IsNullOrWhiteSpace(stage.SummarizerModelName))
            throw new InvalidOperationException("A summarization stage requires both a valid server ID and model name for a separate summarizer.");
        var model = new ServerModel(serverId, stage.SummarizerModelName);
        if (summarizers.TryGetValue(model, out var client))
            return client;
        client = await createOwnedChatClient(model, cancellationToken);
        summarizers.Add(model, client);
        return client;
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
