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

public sealed record CompactionBudget(int ContextWindowTokens, int MaxOutputTokens, int InputBudgetTokens);
