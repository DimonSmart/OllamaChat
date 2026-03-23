using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanStepStatuses
{
    public const string Todo = "todo";
    public const string Running = "running";
    public const string Done = "done";
    public const string Fail = "fail";
    public const string Skip = "skip";
}

public sealed class PlanDefinition
{
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<PlanStep> Steps { get; init; } = new();
}

/// <summary>
/// A single step in the execution plan.
/// Use <see cref="Kind"/> to declare whether the step invokes a registered tool, an ad-hoc LLM call,
/// or a preconfigured saved agent. <see cref="Name"/> identifies the concrete capability for that kind.
/// When <see cref="Kind"/> is <c>llm</c>, the planner must supply <see cref="SystemPrompt"/> and <see cref="UserPrompt"/>.
/// When <see cref="Kind"/> is <c>agent</c>, the planner must supply <see cref="UserPrompt"/> and inputs; the saved agent provides
/// its own system prompt, tool access, and execution settings.
/// </summary>
public sealed class PlanStep
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Step kind: tool, llm, or agent.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Capability identifier for the selected kind.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>System prompt for an LLM step. Required when kind is llm.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>User-level instruction for the LLM or saved-agent step.</summary>
    [JsonPropertyName("userPrompt")]
    public string? UserPrompt { get; init; }

    /// <summary>
    /// Input values. Each input may be:
    /// - a literal JSON value, passed as-is
    /// - a binding object { "from": "$step.ref", "mode": "value|map" }
    /// </summary>
    [JsonPropertyName("in")]
    public Dictionary<string, JsonNode?> In { get; init; } = new();

    /// <summary>
    /// Output contract for this step.
    /// LLM and saved-agent steps must declare the expected result format/shape explicitly.
    /// Tool steps may omit it and rely on the tool output schema.
    /// </summary>
    [JsonPropertyName("out")]
    public PlanStepOutputContract? Out { get; init; }

    [JsonPropertyName("s")]
    public string Status { get; set; } = PlanStepStatuses.Todo;

    [JsonPropertyName("res")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("err")]
    public PlanStepError? Error { get; set; }
}

public sealed class PlanStepError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }
}
