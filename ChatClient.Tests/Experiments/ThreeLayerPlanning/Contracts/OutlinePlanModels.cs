using System.Text.Json.Serialization;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;

public static class OutlineNodeKinds
{
    public const string Discover = "discover";
    public const string Acquire = "acquire";
    public const string Extract = "extract";
    public const string Filter = "filter";
    public const string Rank = "rank";
    public const string Synthesize = "synthesize";
    public const string Answer = "answer";
    public const string Act = "act";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Discover,
        Acquire,
        Extract,
        Filter,
        Rank,
        Synthesize,
        Answer,
        Act
    };
}

public sealed class OutlinePlan
{
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("resultNodeId")]
    public string? ResultNodeId { get; init; }

    [JsonPropertyName("requiredDeliverables")]
    public List<string> RequiredDeliverables { get; init; } = [];

    [JsonPropertyName("nodes")]
    public List<OutlineNode> Nodes { get; init; } = [];

    [JsonIgnore]
    public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockedReason);
}

public sealed class OutlineNode
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; init; } = [];

    [JsonPropertyName("inputs")]
    public List<OutlineNodeInput> Inputs { get; init; } = [];

    [JsonPropertyName("outputs")]
    public List<OutlineNodeOutput> Outputs { get; init; } = [];

    [JsonPropertyName("constraints")]
    public List<string> Constraints { get; init; } = [];

    [JsonPropertyName("notes")]
    public List<string> Notes { get; init; } = [];
}

public sealed class OutlineNodeInput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("semanticType")]
    public string SemanticType { get; init; } = string.Empty;

    [JsonPropertyName("fromNodeId")]
    public string FromNodeId { get; init; } = string.Empty;
}

public sealed class OutlineNodeOutput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("semanticType")]
    public string SemanticType { get; init; } = string.Empty;
}
