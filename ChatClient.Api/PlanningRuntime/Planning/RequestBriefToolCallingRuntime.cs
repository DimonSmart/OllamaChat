using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal sealed class RequestBriefToolCallingRuntime(
    RequestBriefEditingSession session,
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
        AIFunctionFactory.Create(
            (Func<JsonObject>)Read,
            "brief_read",
            "Read the current structured request brief.",
            JsonOptions),
        AIFunctionFactory.Create(
            (Func<string?, string?, JsonObject>)SetScalar,
            "brief_set_scalar",
            "Set one scalar request-brief field. Valid fieldName values: rewrittenRequest, goal, expectedResult, outputExpectations.",
            JsonOptions),
        AIFunctionFactory.Create(
            (Func<string?, string?, JsonObject>)AppendListItem,
            "brief_append_list_item",
            "Append one item to a request-brief list field. Valid listName values: deliverables, constraints, acquisitionNeeds, evidenceRequirements, reasoningNeeds, successCriteria, ambiguityNotes, suggestedPlanOutline.",
            JsonOptions),
        AIFunctionFactory.Create(
            (Func<string?, JsonElement?, JsonObject>)ReplaceList,
            "brief_replace_list",
            "Replace a whole request-brief list field with a new array of strings. Valid listName values: deliverables, constraints, acquisitionNeeds, evidenceRequirements, reasoningNeeds, successCriteria, ambiguityNotes, suggestedPlanOutline.",
            JsonOptions),
        AIFunctionFactory.Create(
            (Func<JsonObject>)Validate,
            "brief_validate",
            "Validate the current request brief and return either ok=true or a structured invalid_brief error.",
            JsonOptions)
    ];

    public JsonArray GetInvocationResultsSnapshot() =>
        new(_invocationResults.Select(node => node?.DeepClone()).ToArray());

    [Description("Read the current structured request brief.")]
    public JsonObject Read() =>
        Execute("brief_read", "brief.read", new JsonObject());

    [Description("Set one scalar request-brief field.")]
    public JsonObject SetScalar(
        [Description("One of rewrittenRequest, goal, expectedResult, outputExpectations.")] string? fieldName = null,
        [Description("New field value. Use empty string to clear outputExpectations only when truly needed.")] string? value = null) =>
        Execute(
            "brief_set_scalar",
            "brief.setScalar",
            new JsonObject
            {
                ["fieldName"] = fieldName,
                ["value"] = value is null ? null : JsonValue.Create(value)
            });

    [Description("Append one item to a request-brief list field.")]
    public JsonObject AppendListItem(
        [Description("One of deliverables, constraints, acquisitionNeeds, evidenceRequirements, reasoningNeeds, successCriteria, ambiguityNotes, suggestedPlanOutline.")] string? listName = null,
        [Description("Non-empty list item to append.")] string? item = null) =>
        Execute(
            "brief_append_list_item",
            "brief.appendListItem",
            new JsonObject
            {
                ["listName"] = listName,
                ["item"] = item
            });

    [Description("Replace a whole request-brief list field with a new array of strings.")]
    public JsonObject ReplaceList(
        [Description("One of deliverables, constraints, acquisitionNeeds, evidenceRequirements, reasoningNeeds, successCriteria, ambiguityNotes, suggestedPlanOutline.")] string? listName = null,
        [Description("Full replacement array of strings.")] JsonElement? items = null) =>
        Execute(
            "brief_replace_list",
            "brief.replaceList",
            new JsonObject
            {
                ["listName"] = listName,
                ["items"] = SerializeElementToNode(items)
            });

    [Description("Validate the current request brief and return either ok=true or a structured invalid_brief error.")]
    public JsonObject Validate() =>
        Execute("brief_validate", "brief.validate", new JsonObject());

    private JsonObject Execute(string actualToolName, string logicalToolName, JsonObject input)
    {
        var loggedInput = input.DeepClone().AsObject();
        executionLogger.Log(
            $"[request-brief] tool:call round={round} name={actualToolName} input={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(loggedInput))}");

        JsonObject result;
        try
        {
            result = session.ExecuteAction(logicalToolName, input);
        }
        catch (Exception ex)
        {
            result = CreateToolFailure("tool_error", ex.Message, logicalToolName);
        }

        InvocationCount++;
        _invocationResults.Add(result.DeepClone());
        executionLogger.Log(
            $"[request-brief] tool:result round={round} name={actualToolName} output={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(result))}");
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
