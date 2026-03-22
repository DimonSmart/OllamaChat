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
    [JsonRequired]
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonRequired]
    [JsonPropertyName("steps")]
    public List<PlanStep> Steps { get; init; } = new();
}

/// <summary>
/// A single step in the execution plan.
/// Use <see cref="Tool"/> for registered workflow building blocks (tools), <see cref="Llm"/> for an ad-hoc LLM call,
/// or <see cref="Agent"/> for invoking a preconfigured saved agent.
/// When <see cref="Llm"/> is set, the planner must supply <see cref="SystemPrompt"/> and <see cref="UserPrompt"/>.
/// When <see cref="Agent"/> is set, the planner must supply <see cref="UserPrompt"/> and inputs; the saved agent provides
/// its own system prompt, tool access, and execution settings.
/// </summary>
public sealed class PlanStep
{
    [JsonRequired]
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Name of a registered workflow building block (tool) to invoke.</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>Logical label for an LLM reasoning call (free text; used only for tracing).</summary>
    [JsonPropertyName("llm")]
    public string? Llm { get; init; }

    /// <summary>Identifier of a preconfigured saved agent callable from planner.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>System prompt for the LLM step. Required when Llm is set.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>User-level instruction for the LLM step. Required when Llm is set.</summary>
    [JsonPropertyName("userPrompt")]
    public string? UserPrompt { get; init; }

    /// <summary>
    /// Input values. Each input may be:
    /// - a literal JSON value, passed as-is
    /// - a binding object { "from": "$step.ref", "mode": "value|map" }
    /// </summary>
    [JsonRequired]
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
