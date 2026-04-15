using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;

public static class LowLevelStepKinds
{
    public const string Tool = "tool";
    public const string Llm = "llm";
    public const string Agent = "agent";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Tool,
        Llm,
        Agent
    };
}

public static class LowLevelFanoutModes
{
    public const string Single = "single";
    public const string PerItem = "per_item";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Single,
        PerItem
    };
}

public static class LowLevelInputSourceKinds
{
    public const string Literal = "literal";
    public const string StepOutputPort = "step_output_port";
}

public static class LowLevelInputModes
{
    public const string Value = "value";
    public const string Map = "map";
}

public static class RuntimeOutputFormats
{
    public const string Json = "json";
    public const string String = "string";
}

public sealed class LowLevelPlan
{
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("outlineResultNodeId")]
    public string? OutlineResultNodeId { get; init; }

    [JsonPropertyName("resultStepId")]
    public string? ResultStepId { get; init; }

    [JsonPropertyName("steps")]
    public List<LowLevelStep> Steps { get; init; } = [];

    [JsonIgnore]
    public bool IsBlocked => !string.IsNullOrWhiteSpace(BlockedReason);
}

public sealed class LowLevelStep
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("outlineNodeId")]
    public string OutlineNodeId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("capabilityId")]
    public string? CapabilityId { get; init; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("inputs")]
    public List<LowLevelStepInput> Inputs { get; init; } = [];

    [JsonPropertyName("outputs")]
    public List<LowLevelStepOutput> Outputs { get; init; } = [];

    [JsonPropertyName("fanout")]
    public string Fanout { get; init; } = LowLevelFanoutModes.Single;

    [JsonPropertyName("out")]
    public LowLevelStepOutputSettings? Out { get; init; }

    [JsonPropertyName("isResult")]
    public bool IsResult { get; init; }
}

public sealed class LowLevelStepInput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public LowLevelInputSource Source { get; init; } = new();
}

public sealed class LowLevelInputSource
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonNode? Value { get; init; }

    [JsonPropertyName("stepId")]
    public string? StepId { get; init; }

    [JsonPropertyName("port")]
    public string? Port { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }
}

public sealed class LowLevelStepOutput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("semanticType")]
    public string SemanticType { get; init; } = string.Empty;
}

public sealed class LowLevelStepOutputSettings
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;
}
