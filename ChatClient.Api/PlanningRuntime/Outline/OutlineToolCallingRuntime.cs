using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChatClient.Api.PlanningRuntime.Outline;

internal sealed class OutlineToolCallingRuntime(
    OutlineEditingSession session,
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
        AIFunctionFactory.Create((Func<JsonObject>)ReadPlan, "outline_read_plan", "Read the current outline plan.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)SetGoal, "outline_set_goal", "Set the outline goal.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)SetBlockedReason, "outline_set_blocked_reason", "Set or clear the blockedReason of the outline plan.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)AppendRequiredDeliverable, "outline_append_required_deliverable", "Append one required deliverable to the outline plan.", JsonOptions),
        AIFunctionFactory.Create((Func<JsonElement?, JsonObject>)ReplaceRequiredDeliverables, "outline_replace_required_deliverables", "Replace the full requiredDeliverables array.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonElement?, JsonObject>)AddNode, "outline_add_node", "Insert one full outline node object after an existing node or at the end.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonElement?, JsonObject>)ReplaceNode, "outline_replace_node", "Replace one existing outline node with a full node object.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)RemoveNode, "outline_remove_node", "Remove one existing outline node.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)LinkNodes, "outline_link_nodes", "Add or replace one logical input edge between two outline nodes.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)MarkResultNode, "outline_mark_result_node", "Mark one existing outline node as the sole result node.", JsonOptions),
        AIFunctionFactory.Create((Func<JsonObject>)Validate, "outline_validate", "Validate the current outline plan.", JsonOptions)
    ];

    public JsonArray GetInvocationResultsSnapshot() =>
        new(_invocationResults.Select(node => node?.DeepClone()).ToArray());

    [Description("Read the current outline plan.")]
    public JsonObject ReadPlan() =>
        Execute("outline_read_plan", "outline.readPlan", new JsonObject());

    [Description("Set the outline goal.")]
    public JsonObject SetGoal(
        [Description("Non-empty outline goal text.")] string? goal = null) =>
        Execute(
            "outline_set_goal",
            "outline.setGoal",
            new JsonObject
            {
                ["goal"] = goal
            });

    [Description("Set or clear the blockedReason of the outline plan.")]
    public JsonObject SetBlockedReason(
        [Description("Blocked reason text. Pass empty string to clear it.")] string? blockedReason = null) =>
        Execute(
            "outline_set_blocked_reason",
            "outline.setBlockedReason",
            new JsonObject
            {
                ["blockedReason"] = blockedReason is null ? null : JsonValue.Create(blockedReason)
            });

    [Description("Append one required deliverable to the outline plan.")]
    public JsonObject AppendRequiredDeliverable(
        [Description("One non-empty deliverable description.")] string? deliverable = null) =>
        Execute(
            "outline_append_required_deliverable",
            "outline.appendRequiredDeliverable",
            new JsonObject
            {
                ["deliverable"] = deliverable
            });

    [Description("Replace the full requiredDeliverables array.")]
    public JsonObject ReplaceRequiredDeliverables(
        [Description("Full replacement array of deliverable strings.")] JsonElement? deliverables = null) =>
        Execute(
            "outline_replace_required_deliverables",
            "outline.replaceRequiredDeliverables",
            new JsonObject
            {
                ["deliverables"] = SerializeElementToNode(deliverables)
            });

    [Description("Insert one full outline node object after an existing node or at the end.")]
    public JsonObject AddNode(
        [Description("Existing node id after which the new node should be inserted. Leave empty to append.")] string? afterNodeId = null,
        [Description("Full outline node object.")] JsonElement? node = null) =>
        Execute(
            "outline_add_node",
            "outline.addNode",
            new JsonObject
            {
                ["afterNodeId"] = afterNodeId is null ? null : JsonValue.Create(afterNodeId),
                ["node"] = SerializeElementToNode(node)
            });

    [Description("Replace one existing outline node with a full node object.")]
    public JsonObject ReplaceNode(
        [Description("Existing node id to replace.")] string? nodeId = null,
        [Description("Full replacement outline node object.")] JsonElement? node = null) =>
        Execute(
            "outline_replace_node",
            "outline.replaceNode",
            new JsonObject
            {
                ["nodeId"] = nodeId,
                ["node"] = SerializeElementToNode(node)
            });

    [Description("Remove one existing outline node.")]
    public JsonObject RemoveNode(
        [Description("Existing node id to remove.")] string? nodeId = null) =>
        Execute(
            "outline_remove_node",
            "outline.removeNode",
            new JsonObject
            {
                ["nodeId"] = nodeId
            });

    [Description("Add or replace one logical input edge between two outline nodes.")]
    public JsonObject LinkNodes(
        [Description("Upstream node id.")] string? fromNodeId = null,
        [Description("Downstream node id.")] string? toNodeId = null,
        [Description("Input name on the downstream node.")] string? inputName = null,
        [Description("Semantic type of the linked input.")] string? semanticType = null) =>
        Execute(
            "outline_link_nodes",
            "outline.linkNodes",
            new JsonObject
            {
                ["fromNodeId"] = fromNodeId,
                ["toNodeId"] = toNodeId,
                ["inputName"] = inputName,
                ["semanticType"] = semanticType
            });

    [Description("Mark one existing outline node as the sole result node.")]
    public JsonObject MarkResultNode(
        [Description("Existing node id to mark as the result node.")] string? nodeId = null) =>
        Execute(
            "outline_mark_result_node",
            "outline.markResultNode",
            new JsonObject
            {
                ["nodeId"] = nodeId
            });

    [Description("Validate the current outline plan.")]
    public JsonObject Validate() =>
        Execute("outline_validate", "outline.validate", new JsonObject());

    private JsonObject Execute(string actualToolName, string logicalToolName, JsonObject input)
    {
        var loggedInput = input.DeepClone().AsObject();
        executionLogger.Log(
            $"[outline] tool:call round={round} name={actualToolName} input={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(loggedInput))}");

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
            $"[outline] tool:result round={round} name={actualToolName} output={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(result))}");
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
