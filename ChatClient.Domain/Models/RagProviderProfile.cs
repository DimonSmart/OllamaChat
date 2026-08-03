using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RagSearchMode
{
    Auto,
    OnDemand,
    BeforeInvoke
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RagRetrievalStrategy
{
    Vector,
    Hybrid
}

public sealed class RagProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public RagSearchMode SearchMode { get; set; } = RagSearchMode.Auto;
    public RagRetrievalStrategy RetrievalStrategy { get; set; } = RagRetrievalStrategy.Hybrid;
    public int MaxResults { get; set; } = 5;
    public double? MinRelevanceScore { get; set; } = 0.7;
    public int RecentMessageMemoryLimit { get; set; } = 6;
    public bool IncludeAssistantMessages { get; set; } = true;
    public int MaxRetrievedContextTokens { get; set; } = 4000;
    public int AdjacentChunkCount { get; set; } = 1;
    public string? FunctionToolDescription { get; set; }
    public string? AdditionalContextInstructions { get; set; }
    public bool RequestCitations { get; set; } = true;
    public string? CitationsPrompt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
