namespace ChatClient.Domain.Models;

public static class CompactionProfileKinds
{
    public const string ContextWindow = "context-window";
    public const string CustomPipeline = "custom-pipeline";
}

public static class CompactionBudgetSources
{
    public const string SelectedModel = "selected-model";
    public const string Fixed = "fixed";
}

public static class CompactionStageKinds
{
    public const string ToolResult = "tool-result";
    public const string Truncation = "truncation";
    public const string Summarization = "summarization";
    public const string SlidingWindow = "sliding-window";
}

public static class CompactionLimitKinds
{
    public const string InputBudgetPercent = "input-budget-percent";
    public const string Tokens = "tokens";
    public const string Messages = "messages";
    public const string Turns = "turns";
    public const string Groups = "groups";
}

public sealed class CompactionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = CompactionProfileKinds.ContextWindow;
    public string BudgetSource { get; set; } = CompactionBudgetSources.SelectedModel;
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public double ToolResultThreshold { get; set; } = .50;
    public double TruncationThreshold { get; set; } = .80;
    public List<CompactionStage> Stages { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class CompactionStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = string.Empty;
    public CompactionLimit Trigger { get; set; } = new();
    public CompactionLimit Target { get; set; } = new();
    public int MinimumPreservedGroups { get; set; }
    public int MinimumPreservedTurns { get; set; }
    public string? SummaryInstructions { get; set; }
    public Guid? SummarizerLlmId { get; set; }
    public string? SummarizerModelName { get; set; }
}

public sealed class CompactionLimit
{
    public string Kind { get; set; } = CompactionLimitKinds.Tokens;
    public double Value { get; set; }
}

public static class CompactionStageDefaults
{
    public static CompactionStage Create(string kind) => kind switch
    {
        CompactionStageKinds.ToolResult => Create(kind, CompactionLimitKinds.InputBudgetPercent, .45, .35, minimumPreservedGroups: 8),
        CompactionStageKinds.Truncation => Create(kind, CompactionLimitKinds.InputBudgetPercent, .80, .70, minimumPreservedGroups: 4),
        CompactionStageKinds.Summarization => Create(kind, CompactionLimitKinds.InputBudgetPercent, .65, .50, minimumPreservedGroups: 8),
        CompactionStageKinds.SlidingWindow => Create(kind, CompactionLimitKinds.Turns, 20, 12, minimumPreservedTurns: 8),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown compaction stage kind.")
    };

    private static CompactionStage Create(string stageKind, string limitKind, double trigger, double target, int minimumPreservedGroups = 0, int minimumPreservedTurns = 0) => new()
    {
        Kind = stageKind,
        Trigger = new CompactionLimit { Kind = limitKind, Value = trigger },
        Target = new CompactionLimit { Kind = limitKind, Value = target },
        MinimumPreservedGroups = minimumPreservedGroups,
        MinimumPreservedTurns = minimumPreservedTurns
    };
}

public static class CompactionPolicySummary
{
    public static string FormatPolicy(CompactionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Kind == CompactionProfileKinds.ContextWindow
            ? $"Context window: tool results {profile.ToolResultThreshold * 100:0.##}%, history {profile.TruncationThreshold * 100:0.##}%"
            : string.Join(" → ", profile.Stages.Select(static stage => StageLabel(stage.Kind)));
    }

    public static string FormatAbsoluteThresholds(CompactionProfile profile, CompactionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Kind == CompactionProfileKinds.ContextWindow)
        {
            return $"tool results={ResolveContextWindowThreshold(profile.ToolResultThreshold, budget.InputBudgetTokens)}, " +
                   $"history={ResolveContextWindowThreshold(profile.TruncationThreshold, budget.InputBudgetTokens)}";
        }

        return string.Join("; ", profile.Stages.Select(stage =>
            $"{StageLabel(stage.Kind)}={FormatLimit(stage.Trigger, budget)}→{FormatLimit(stage.Target, budget)}"));
    }

    private static string StageLabel(string kind) => kind switch
    {
        CompactionStageKinds.ToolResult => "tool results",
        CompactionStageKinds.Truncation => "history",
        CompactionStageKinds.Summarization => "summary",
        CompactionStageKinds.SlidingWindow => "sliding window",
        _ => kind
    };

    private static string FormatLimit(CompactionLimit limit, CompactionBudget budget) =>
        limit.Kind == CompactionLimitKinds.InputBudgetPercent
            ? $"{ResolveInputBudgetPercent(limit.Value, budget.InputBudgetTokens)} ({limit.Value * 100:0}%)"
            : $"{limit.Value:0.##} {limit.Kind}";

    private static int ResolveInputBudgetPercent(double percent, int inputBudgetTokens) =>
        checked((int)Math.Floor(inputBudgetTokens * percent));

    private static int ResolveContextWindowThreshold(double threshold, int inputBudgetTokens) =>
        checked((int)Math.Floor(inputBudgetTokens * threshold));
}
