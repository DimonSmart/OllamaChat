using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.Services;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal sealed class PlanToolCallingRuntime(
    PlanEditingSession session,
    IReadOnlyCollection<AppToolDescriptor> workflowTools,
    string logPrefix,
    int round,
    IExecutionLogger executionLogger,
    Func<string?, JsonObject>? runtimeReadFailedTrace = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly JsonArray _invocationResults = [];

    public int InvocationCount { get; private set; }

    public IReadOnlyList<AITool> CreateTools(bool includeRuntimeReadFailedTrace)
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(
                (Func<string?, JsonObject>)ReadStep,
                PlanningAgentToolNames.PlanReadStep,
                "Read the current JSON of one step in the working plan.",
                JsonOptions),
            AIFunctionFactory.Create(
                (Func<string?, JsonElement?, JsonObject>)ReplaceStep,
                PlanningAgentToolNames.PlanReplaceStep,
                "Replace one existing step in place. Provide the existing stepId and the FULL replacement plan step object.",
                JsonOptions),
            AIFunctionFactory.Create(
                (Func<string?, JsonElement?, JsonObject>)AddSteps,
                PlanningAgentToolNames.PlanAddSteps,
                "Insert one or more FULL plan step objects after an existing step. Use afterStepId=null only when rebuilding an empty plan.",
                JsonOptions),
            AIFunctionFactory.Create(
                (Func<string?, JsonObject>)ResetFrom,
                PlanningAgentToolNames.PlanResetFrom,
                "Reset execution state for the specified step and everything downstream.",
                JsonOptions),
            AIFunctionFactory.Create(
                (Func<JsonObject>)ValidateDraft,
                PlanningAgentToolNames.PlanValidateDraft,
                "Validate the current working plan draft and return either ok=true or a structured invalid_plan error.",
                JsonOptions)
        ];

        if (includeRuntimeReadFailedTrace && runtimeReadFailedTrace is not null)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<string?, JsonObject>)ReadFailedTrace,
                PlanningAgentToolNames.RuntimeReadFailedTrace,
                "Read a compact structured summary of one failed execution trace.",
                JsonOptions));
        }

        return tools;
    }

    [Description("Read the current JSON of one step in the working plan.")]
    public JsonObject ReadStep(
        [Description("Existing step id to inspect.")] string? stepId = null)
        => ExecuteSessionTool(
            PlanningAgentToolNames.PlanReadStep,
            "plan.readStep",
            new JsonObject
            {
                ["stepId"] = stepId
            });

    [Description("Replace one existing step in place. Provide the existing stepId and the FULL replacement plan step object.")]
    public JsonObject ReplaceStep(
        [Description("Existing step id to replace.")] string? stepId = null,
        [Description("Full replacement plan step object with id, tool or llm, inputs, and optional prompts/output schema.")] JsonElement? step = null)
        => ExecuteSessionTool(
            PlanningAgentToolNames.PlanReplaceStep,
            "plan.replaceStep",
            new JsonObject
            {
                ["stepId"] = stepId,
                ["step"] = SerializeElementToNode(step)
            });

    [Description("Insert one or more FULL plan step objects after an existing step. Use afterStepId=null only when rebuilding an empty plan.")]
    public JsonObject AddSteps(
        [Description("Existing step id after which the new steps should be inserted.")] string? afterStepId = null,
        [Description("Array of FULL plan step objects to insert.")] JsonElement? steps = null)
        => ExecuteSessionTool(
            PlanningAgentToolNames.PlanAddSteps,
            "plan.addSteps",
            new JsonObject
            {
                ["afterStepId"] = afterStepId is null ? null : JsonValue.Create(afterStepId),
                ["steps"] = SerializeElementToNode(steps)
            });

    [Description("Reset execution state for the specified step and everything downstream.")]
    public JsonObject ResetFrom(
        [Description("Step id from which execution state should be reset.")] string? stepId = null)
        => ExecuteSessionTool(
            PlanningAgentToolNames.PlanResetFrom,
            "plan.resetFrom",
            new JsonObject
            {
                ["stepId"] = stepId
            });

    [Description("Validate the current working plan draft and return either ok=true or a structured invalid_plan error.")]
    public JsonObject ValidateDraft()
        => ExecuteCustomTool(
            PlanningAgentToolNames.PlanValidateDraft,
            "plan.validateDraft",
            new JsonObject(),
            static (session, workflowTools, _) => PlanDraftValidationTool.CreateValidationResult(session, workflowTools));

    [Description("Read a compact structured summary of one failed execution trace.")]
    public JsonObject ReadFailedTrace(
        [Description("Failed step id to inspect.")] string? stepId = null)
        => ExecuteCustomTool(
            PlanningAgentToolNames.RuntimeReadFailedTrace,
            "runtime.readFailedTrace",
            new JsonObject
            {
                ["stepId"] = stepId
            },
            (_, _, input) => runtimeReadFailedTrace?.Invoke(input["stepId"]?.GetValue<string>())
                ?? CreateToolFailure("tool_error", "runtime.readFailedTrace is not available.", "runtime.readFailedTrace"));

    public JsonArray GetInvocationResultsSnapshot() =>
        new(_invocationResults.Select(node => node?.DeepClone()).ToArray());

    private JsonObject ExecuteSessionTool(string actualToolName, string logicalToolName, JsonObject input) =>
        ExecuteCustomTool(
            actualToolName,
            logicalToolName,
            input,
            (currentSession, _, toolInput) => currentSession.ExecuteAction(logicalToolName, toolInput));

    private JsonObject ExecuteCustomTool(
        string actualToolName,
        string logicalToolName,
        JsonObject input,
        Func<PlanEditingSession, IReadOnlyCollection<AppToolDescriptor>, JsonObject, JsonObject> executor)
    {
        var loggedInput = input.DeepClone().AsObject();
        executionLogger.Log(
            $"[{logPrefix}] tool:call round={round} name={actualToolName} input={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(loggedInput))}");

        JsonObject result;
        try
        {
            result = executor(session, workflowTools, input);
        }
        catch (Exception ex)
        {
            result = CreateToolFailure("tool_error", ex.Message, logicalToolName);
        }

        InvocationCount++;
        _invocationResults.Add(result.DeepClone());
        executionLogger.Log(
            $"[{logPrefix}] tool:result round={round} name={actualToolName} output={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(result))}");

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
