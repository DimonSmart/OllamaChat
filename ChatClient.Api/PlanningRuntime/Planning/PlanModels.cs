using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanStepStatuses
{
    public const string Todo = "todo";
    public const string Running = "running";
    public const string Done = "done";
    public const string Partial = "partial";
    public const string Fail = "fail";
    public const string Skip = "skip";
}

public sealed class PlanDefinition
{
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<PlanStep> Steps { get; init; } = new();

    /// <summary>
    /// Result contract derived from the request brief during the clarification phase.
    /// Describes what the final result must look like and is used by the validator
    /// and repair loop to verify semantic completeness.
    /// Null when the plan was created without a prior clarification phase.
    /// </summary>
    [JsonIgnore]
    public ResultContract? ResultContract { get; init; }

    /// <summary>
    /// When the planner cannot satisfy the request with the available capabilities,
    /// this field carries a human-readable explanation of the blocking reason.
    /// A non-null value signals that the plan is structurally complete but
    /// intentionally blocked at the planning level.
    /// </summary>
    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; init; }
}

/// <summary>
/// A single step in the execution plan.
/// Use <see cref="Kind"/> to declare whether the step invokes a registered tool, an ad-hoc LLM call,
/// or a preconfigured saved agent. <see cref="CapabilityId"/> identifies the concrete capability for that kind.
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

    /// <summary>
    /// Capability identifier for the selected kind.
    /// Required for tool and saved-agent steps.
    /// Optional for generic llm steps, where it acts only as a display label.
    /// </summary>
    [JsonPropertyName("capabilityId")]
    public string? CapabilityId { get; init; }

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
    /// LLM and saved-agent steps declare the expected result format/shape explicitly.
    /// Tool steps should omit it and rely on the tool output schema owned by the runtime.
    /// </summary>
    [JsonPropertyName("out")]
    public PlanStepOutputContract? Out { get; init; }

    /// <summary>
    /// Explicit result designation. Exactly one step in the plan must have this set to <c>true</c>:
    /// the step whose output is the final user-visible or machine-visible result.
    /// Set by the planner when generating the draft; auto-marked on the last terminal step
    /// by the normalizer when the planner omits it.
    /// </summary>
    [JsonPropertyName("isResult")]
    public bool IsResult { get; set; }

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
