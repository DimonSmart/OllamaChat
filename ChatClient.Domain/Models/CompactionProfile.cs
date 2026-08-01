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

public sealed class CompactionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = CompactionProfileKinds.ContextWindow;
    public string BudgetSource { get; set; } = CompactionBudgetSources.SelectedModel;
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public List<CompactionStage> Stages { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class CompactionStage
{
    public string Kind { get; set; } = string.Empty;
    public int TriggerTokenCount { get; set; }
    public int TargetTokenCount { get; set; }
    public string? SummaryInstructions { get; set; }
    public Guid? SummarizerLlmId { get; set; }
    public string? SummarizerModelName { get; set; }
}
