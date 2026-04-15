using ChatClient.Api.PlanningRuntime.LowLevel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Runtime;

public static class RuntimeInputValueKinds
{
    public const string Literal = "literal";
    public const string Binding = "binding";
}

public sealed class RuntimePlan
{
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("resultStepId")]
    public string ResultStepId { get; init; } = string.Empty;

    [JsonPropertyName("resultPort")]
    public string ResultPort { get; init; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<RuntimeStep> Steps { get; init; } = [];
}

public sealed class RuntimeStep
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("capabilityId")]
    public string? CapabilityId { get; init; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("instruction")]
    public string? Instruction { get; init; }

    [JsonPropertyName("fanout")]
    public string Fanout { get; init; } = LowLevelFanoutModes.Single;

    [JsonPropertyName("in")]
    public Dictionary<string, RuntimeInputValue> In { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("outputs")]
    public List<RuntimeStepOutput> Outputs { get; init; } = [];

    [JsonPropertyName("out")]
    public RuntimeStepOutputSettings? Out { get; init; }

    [JsonPropertyName("isResult")]
    public bool IsResult { get; init; }
}

public sealed class RuntimeInputValue
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("literal")]
    public JsonNode? Literal { get; init; }

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }
}

public sealed class RuntimeStepOutput
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("semanticType")]
    public string SemanticType { get; init; } = string.Empty;
}

public sealed class RuntimeStepOutputSettings
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;
}
