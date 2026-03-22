using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal static class PlanDraftValidationTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject CreateValidationResult(
        PlanEditingSession session,
        IReadOnlyCollection<AppToolDescriptor> workflowTools,
        PlanningCallableAgentCatalog? agentCatalog = null)
    {
        try
        {
            var draft = session.BuildPlan();
            if (PlanValidator.TryValidate(draft, workflowTools, agentCatalog?.ListAgents(), out var validationIssue))
            {
                return new JsonObject
                {
                    ["tool"] = "plan.validateDraft",
                    ["ok"] = true
                };
            }

            return new JsonObject
            {
                ["tool"] = "plan.validateDraft",
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "invalid_plan",
                    ["message"] = validationIssue!.Message,
                    ["details"] = JsonSerializer.SerializeToNode(validationIssue, JsonOptions)
                }
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["tool"] = "plan.validateDraft",
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "invalid_plan",
                    ["message"] = ex.Message
                }
            };
        }
    }
}
