namespace ChatClient.Domain.Models;

public sealed class ModelRuntimeLimits
{
    public Guid ServerId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public int ContextWindowTokens { get; set; }
    public int MaxOutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ModelRuntimeLimitValidation
{
    public static bool HasValidTokenBudget(int contextWindowTokens, int maxOutputTokens) =>
        contextWindowTokens > 0 && maxOutputTokens > 0 && maxOutputTokens < contextWindowTokens;

    public static bool HasValidTokenBudget(int? contextWindowTokens, int? maxOutputTokens) =>
        contextWindowTokens is int context && maxOutputTokens is int output &&
        HasValidTokenBudget(context, output);
}

public sealed record CompactionBudget(int ContextWindowTokens, int MaxOutputTokens, int InputBudgetTokens);
