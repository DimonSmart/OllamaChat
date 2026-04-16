using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.Services;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

internal sealed class LowLevelToolCallingRuntime(
    LowLevelEditingSession session,
    IReadOnlyCollection<AppToolDescriptor> tools,
    int round,
    IExecutionLogger executionLogger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly JsonArray _invocationResults = [];

    public int InvocationCount { get; private set; }

    public IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create((Func<JsonObject>)ReadPlan, "low_read_plan", "Read the current low-level plan.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)ReadStep, "low_read_step", "Read one existing low-level step by id.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)SetGoal, "low_set_goal", "Set the low-level plan goal.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)SetBlockedReason, "low_set_blocked_reason", "Set or clear the blockedReason of the low-level plan.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonElement?, JsonObject>)AddStep, "low_add_step", "Insert one full low-level step object after an existing step or at the end.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonElement?, JsonObject>)ReplaceStep, "low_replace_step", "Replace one existing low-level step with a full step object.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)RemoveStep, "low_remove_step", "Remove one existing low-level step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonElement?, JsonObject>)RewireInput, "low_rewire_input", "Replace one input source on an existing low-level step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)MarkResultStep, "low_mark_result_step", "Mark one existing low-level step as the sole result step.", JsonOptions),
        AIFunctionFactory.Create((Func<JsonObject>)Validate, "low_validate", "Validate the current low-level plan.", JsonOptions)
    ];

    public JsonArray GetInvocationResultsSnapshot() =>
        new(_invocationResults.Select(node => node?.DeepClone()).ToArray());

    [Description("Read the current low-level plan.")]
    public JsonObject ReadPlan() =>
        Execute("low_read_plan", "low.readPlan", new JsonObject());

    [Description("Read one existing low-level step by id.")]
    public JsonObject ReadStep(
        [Description("Existing step id to inspect.")] string? stepId = null) =>
        Execute(
            "low_read_step",
            "low.readStep",
            new JsonObject
            {
                ["stepId"] = stepId
            });

    [Description("Set the low-level plan goal.")]
    public JsonObject SetGoal(
        [Description("Non-empty low-level plan goal text.")] string? goal = null) =>
        Execute(
            "low_set_goal",
            "low.setGoal",
            new JsonObject
            {
                ["goal"] = goal
            });

    [Description("Set or clear the blockedReason of the low-level plan.")]
    public JsonObject SetBlockedReason(
        [Description("Blocked reason text. Pass empty string to clear it.")] string? blockedReason = null) =>
        Execute(
            "low_set_blocked_reason",
            "low.setBlockedReason",
            new JsonObject
            {
                ["blockedReason"] = blockedReason is null ? null : JsonValue.Create(blockedReason)
            });

    [Description("Insert one full low-level step object after an existing step or at the end.")]
    public JsonObject AddStep(
        [Description("Existing step id after which the new step should be inserted. Leave empty to append.")] string? afterStepId = null,
        [Description("Full low-level step object.")] JsonElement? step = null) =>
        Execute(
            "low_add_step",
            "low.addStep",
            new JsonObject
            {
                ["afterStepId"] = afterStepId is null ? null : JsonValue.Create(afterStepId),
                ["step"] = SerializeElementToNode(step)
            });

    [Description("Replace one existing low-level step with a full step object.")]
    public JsonObject ReplaceStep(
        [Description("Existing step id to replace.")] string? stepId = null,
        [Description("Full replacement low-level step object.")] JsonElement? step = null) =>
        Execute(
            "low_replace_step",
            "low.replaceStep",
            new JsonObject
            {
                ["stepId"] = stepId,
                ["step"] = SerializeElementToNode(step)
            });

    [Description("Remove one existing low-level step.")]
    public JsonObject RemoveStep(
        [Description("Existing step id to remove.")] string? stepId = null) =>
        Execute(
            "low_remove_step",
            "low.removeStep",
            new JsonObject
            {
                ["stepId"] = stepId
            });

    [Description("Replace one input source on an existing low-level step.")]
    public JsonObject RewireInput(
        [Description("Existing step id to update.")] string? stepId = null,
        [Description("Input name to replace.")] string? inputName = null,
        [Description("Full low-level input source object.")] JsonElement? source = null) =>
        Execute(
            "low_rewire_input",
            "low.rewireInput",
            new JsonObject
            {
                ["stepId"] = stepId,
                ["inputName"] = inputName,
                ["source"] = SerializeElementToNode(source)
            });

    [Description("Mark one existing low-level step as the sole result step.")]
    public JsonObject MarkResultStep(
        [Description("Existing step id to mark as the result step.")] string? stepId = null) =>
        Execute(
            "low_mark_result_step",
            "low.markResultStep",
            new JsonObject
            {
                ["stepId"] = stepId
            });

    [Description("Validate the current low-level plan.")]
    public JsonObject Validate() =>
        Execute("low_validate", "low.validate", new JsonObject());

    private JsonObject Execute(string actualToolName, string logicalToolName, JsonObject input)
    {
        var loggedInput = input.DeepClone().AsObject();
        executionLogger.Log(
            $"[low-level] tool:call round={round} name={actualToolName} input={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(loggedInput))}");

        JsonObject result;
        try
        {
            result = string.Equals(logicalToolName, "low.validate", StringComparison.Ordinal)
                ? session.Validate(tools)
                : session.ExecuteAction(logicalToolName, input);
        }
        catch (Exception ex)
        {
            result = CreateToolFailure("tool_error", ex.Message, logicalToolName);
        }

        InvocationCount++;
        _invocationResults.Add(result.DeepClone());
        executionLogger.Log(
            $"[low-level] tool:result round={round} name={actualToolName} output={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(result))}");
        return result;
    }

    private static JsonObject CreateToolFailure(string code, string message, string? toolName = null) => new()
    {
        ["tool"] = toolName,
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static JsonNode? SerializeElementToNode(JsonElement? element) =>
        element is null ? null : JsonSerializer.SerializeToNode(element.Value, JsonOptions);
}
