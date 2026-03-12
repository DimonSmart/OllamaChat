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
/// Use <see cref="Tool"/> for registered workflow building blocks (tools), or <see cref="Llm"/> for an ad-hoc LLM call.
/// When <see cref="Llm"/> is set, the planner must supply <see cref="SystemPrompt"/> and <see cref="UserPrompt"/>.
/// The executor simply forwards them to the LLM; there is no pre-registered agent registry.
/// LLM steps have NO access to external systems; they can only process data from earlier tool steps.
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

    /// <summary>System prompt for the LLM step. Required when Llm is set.</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; init; }

    /// <summary>User-level instruction for the LLM step. Required when Llm is set.</summary>
    [JsonPropertyName("userPrompt")]
    public string? UserPrompt { get; init; }

    /// <summary>Input bindings. String values starting with '$' are references to previous step outputs.</summary>
    [JsonRequired]
    [JsonPropertyName("in")]
    public Dictionary<string, JsonNode?> In { get; init; } = new();

    /// <summary>Output type hint: "json" (default) or "string".</summary>
    [JsonPropertyName("out")]
    public string? Out { get; init; }

    /// <summary>
    /// When true, and the resolved input is a JsonArray, the agent is called once per element
    /// and results are collected into an array (fan-out / map). Leave false (default) to pass
    /// the array as-is to the agent (reduce / batch). Ignored for tool steps (schema-driven).
    /// </summary>
    [JsonPropertyName("each")]
    public bool Each { get; init; }

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
