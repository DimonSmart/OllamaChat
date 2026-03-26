using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class LlmInitialDraftRepairer(
    IChatClient chatClient,
    PlanningToolCatalog toolCatalog,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null,
    PlanningCallableAgentCatalog? agentCatalog = null) : IInitialDraftRepairer
{
    private const int MaxRounds = 6;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private readonly PlanningCallableAgentCatalog _agentCatalog = agentCatalog ?? PlanningCallableAgentCatalog.Empty;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlanDefinition> RepairAsync(
        InitialDraftRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        var workflowTools = toolCatalog.ListTools();
        var session = new PlanEditingSession(ClonePlan(request.DraftPlan), PlanModelProfile.Draft);
        var systemPrompt = BuildSystemPrompt(workflowTools);
        JsonArray? lastToolResults = null;
        string? retryMessage = null;

        _log.Log($"[draft-repair] start attempt={request.AttemptNumber} reason={Shorten(request.ValidationIssue.Message, 240)} issue={PlanningJson.SerializeCompact(request.ValidationIssue)}");
        _observer.OnEvent(new DiagnosticPlanRunEvent("initial_draft_repairer", $"Start: {Shorten(request.ValidationIssue.Message, 240)}"));

        for (var round = 1; round <= MaxRounds; round++)
        {
            var runtime = new PlanToolCallingRuntime(
                session,
                workflowTools,
                _agentCatalog,
                "draft-repair",
                round,
                _log);
            var agent = new ChatClientAgent(
                chatClient,
                systemPrompt,
                "initial_draft_repairer",
                null,
                runtime.CreateTools(includeRuntimeReadFailedTrace: false).ToList(),
                null,
                null);
            var roundPrompt = BuildRoundPrompt(request, session, round, lastToolResults, retryMessage);

            try
            {
                var completionText = await GenerateRoundCompletionAsync(agent, roundPrompt, cancellationToken);
                lastToolResults = runtime.GetInvocationResultsSnapshot();

                if (runtime.InvocationCount == 0)
                    throw new InvalidOperationException("Initial draft repairer must use at least one plan-editing tool before replying.");

                _log.Log($"[draft-repair] round={round} toolCalls={runtime.InvocationCount} completion={Shorten(completionText, 240)}");
                _log.Log($"[draft-repair] round={round} toolResults={SerializeDetailedSummary(lastToolResults)}");

                var validationResult = PlanDraftValidationTool.CreateValidationResult(session, workflowTools, _agentCatalog);
                lastToolResults.Add(validationResult);
                _log.Log($"[draft-repair] round={round} validation={SerializeDetailedSummary(validationResult)}");

                if (validationResult["ok"]?.GetValue<bool>() == true)
                {
                    var repaired = session.BuildPlan();
                    _log.Log($"[draft-repair] success steps={repaired.Steps.Count} goal={Shorten(repaired.Goal, 240)}");
                    _log.Log($"[draft-repair] summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(repaired, PlanModelProfile.Draft))}");
                    return repaired;
                }

                retryMessage = validationResult["error"]?["message"]?.GetValue<string>()
                    ?? "The working draft is still invalid.";
            }
            catch (Exception ex) when (round < MaxRounds)
            {
                lastToolResults = runtime.GetInvocationResultsSnapshot();
                retryMessage = ex.Message;
                _log.Log($"[draft-repair] round={round} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("initial_draft_repairer", $"Round {round} failed: {Shorten(ex.Message, 240)}"));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Initial draft repair could not produce a valid plan after {round} rounds. Last error: {ex.Message}",
                    ex);
            }
        }

        throw new InvalidOperationException($"Initial draft repair could not produce a valid plan after {MaxRounds} rounds.");
    }

    private static async Task<string> GenerateRoundCompletionAsync(
        ChatClientAgent agent,
        string roundPrompt,
        CancellationToken cancellationToken)
    {
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            ToolMode = ChatToolMode.RequireAny,
            AllowMultipleToolCalls = true
        });
        var response = await agent.RunAsync(
            roundPrompt,
            null,
            runOptions,
            cancellationToken);
        return response.Text?.Trim() ?? string.Empty;
    }

    private string BuildSystemPrompt(IReadOnlyCollection<AppToolDescriptor> workflowTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an initial draft repair agent.");
        sb.AppendLine("You do NOT generate a full plan from scratch.");
        sb.AppendLine("You repair an invalid planning draft before any execution starts by using real plan-editing tools.");
        sb.AppendLine("Use the tools directly, then finish with one short plain-text completion note. Do not return JSON.");
        sb.AppendLine();
        sb.AppendLine("You MUST inspect or edit the working plan by calling tools. Do not describe edit actions in JSON.");
        sb.AppendLine();
        sb.AppendLine("Available plan-editing tools:");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanReadStep}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanReplaceStep}(stepId, step)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanAddSteps}(afterStepId, steps)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanRemoveStep}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanResetFrom}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanValidateDraft}()");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use the invalid draft plan as the source of truth.");
        sb.AppendLine("- Prefer the smallest correct repair.");
        sb.AppendLine("- Repair the validator complaint directly instead of rewriting unrelated steps.");
        sb.AppendLine("- Preserve correct step ids, bindings, prompts, and working structure whenever possible.");
        sb.AppendLine($"- The ONLY valid way to mutate the draft is by calling {PlanningAgentToolNames.PlanReplaceStep}, {PlanningAgentToolNames.PlanAddSteps}, or {PlanningAgentToolNames.PlanRemoveStep}.");
        sb.AppendLine($"- For {PlanningAgentToolNames.PlanReplaceStep}, the 'step' argument must be the FULL replacement plan step object, not a diff and not a summary.");
        sb.AppendLine("- Never paste previous tool outputs like before/after/diff/position back into a replacement step.");
        sb.AppendLine($"- Use {PlanningAgentToolNames.PlanRemoveStep}(stepId) to delete dead, duplicate, or structurally invalid steps instead of layering more placeholders on top.");
        sb.AppendLine("- If the issue is a tool schema mismatch, fix the exact tool input key, binding path, or literal shape required by that tool.");
        sb.AppendLine("- If a normalization or shortlist step removed fields that a downstream tool still needs, repair that projection by preserving the required source records or rebinding from the original producer instead of deleting the downstream evidence-gathering steps.");
        sb.AppendLine("- If tool metadata says discovered records are directly compatible with a downstream tool input, keep or restore those full compatible records instead of reducing them to partial summaries like {url,name}.");
        sb.AppendLine("- If the issue is a binding path or future-step reference, repair the dependency graph rather than inventing new unrelated steps.");
        sb.AppendLine("- Keep the repaired plan capability-aware: external capabilities gather facts or perform actions; llm steps only transform, normalize, compare, or synthesize the available evidence.");
        sb.AppendLine("- The listed capabilities may support any domain or action. Repair the plan by following actual tool descriptions, schemas, and compatibility hints instead of assuming a fixed workflow.");
        sb.AppendLine("- If the user request needs external facts or actions, make sure the plan maps them to listed capabilities instead of leaving them implied.");
        sb.AppendLine("- If the user asked for a minimum count of distinct items and the draft currently cannot supply that count, preserve or add the upstream discovery/acquisition path that could gather them, such as increasing a tool limit, broadening acquisition coverage, or adding another relevant acquisition step when tool metadata says one call may return fewer than requested. Do not solve a count gap by only weakening the final writer.");
        sb.AppendLine("- If the listed capabilities cannot obtain or verify a required deliverable, prefer a short blocked plan over a fake executable plan.");
        sb.AppendLine("- Match tool input keys and shapes exactly to each tool schema. If one tool returns references or records and another tool accepts one record at a time, bind the matching object shape instead of inventing projections.");
        sb.AppendLine($"- Use {PlanningAgentToolNames.PlanReadStep}(stepId) when you need to inspect one step precisely.");
        sb.AppendLine($"- Use {PlanningAgentToolNames.PlanValidateDraft}() before your final completion note.");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanResetFrom}(stepId) is available but should only be used when you intentionally want to clear downstream execution state.");
        sb.AppendLine("- When you are confident the draft is ready, return one short plain-text sentence like 'Draft repaired.'");
        sb.AppendLine();
        sb.AppendLine("Plan step rules:");
        sb.AppendLine("- Every step must include id, kind, and in.");
        sb.AppendLine("- kind must be exactly one of 'tool', 'llm', or 'agent'.");
        sb.AppendLine("- Tool and saved-agent steps must include capabilityId.");
        sb.AppendLine("- For tool or saved-agent steps, capabilityId must be the exact tool id or saved-agent id from the catalog.");
        sb.AppendLine("- For generic llm steps, capabilityId is optional.");
        sb.AppendLine("- LLM steps must have systemPrompt and userPrompt.");
        sb.AppendLine("- Saved-agent steps must have userPrompt, must not have systemPrompt, and must use one callable agent id listed below.");
        sb.AppendLine("- Tool steps must use the exact workflow tool id listed below, including any server prefix.");
        sb.AppendLine("- Put dynamic inputs under 'in' using binding objects like {\"from\":\"$step.ref\",\"mode\":\"value|map\"} or concat bindings like {\"concat\":[{\"from\":\"$s1.items\",\"mode\":\"value\"},{\"from\":\"$s2.items\",\"mode\":\"value\"}],\"type\":\"array<object>\"}.");
        sb.AppendLine("- Binding field 'type' is optional. Add it only when the repair specifically needs an inline input hint.");
        sb.AppendLine("- binding.mode supports ONLY 'value' or 'map'. Never use 'flatten' as a binding mode.");
        sb.AppendLine("- Invalid example: {\"from\":\"$s1.items[]\",\"mode\":\"flatten\"}.");
        sb.AppendLine("- Use concat only to merge multiple array sources into one array input. Every concat item must resolve to an array and must use mode='value'.");
        sb.AppendLine("- Every non-final step must feed at least one downstream consumer. The only terminal step must be the last step.");
        sb.AppendLine("- Literal tool inputs must be plain JSON literals. Never wrap them in helper objects like {\"value\":...}.");
        sb.AppendLine("- If a tool returns an object containing an array field, bind from that field directly.");
        sb.AppendLine("- If a downstream tool needs a projected array field, express it explicitly in the ref.");
        sb.AppendLine("- Tool steps must not declare out. The runtime derives tool output contracts from the tool catalog.");
        sb.AppendLine("- LLM and saved-agent steps must declare out.format.");
        sb.AppendLine("- out.schema is optional. Add it only when the repair needs a stronger explicit output contract.");
        sb.AppendLine("- Every schema node must declare either type or enum.");
        sb.AppendLine("- If out.schema.type='object', every entry inside out.schema.properties must itself declare type or enum.");
        sb.AppendLine("- If out.schema.type='array', out.schema.items must declare type or enum.");
        sb.AppendLine("- If out.format='string', either omit out.schema or use out.schema={\"type\":\"string\"}.");
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendTools(sb, workflowTools);
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendAgents(sb, _agentCatalog.ListAgents());

        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        InitialDraftRepairRequest request,
        PlanEditingSession session,
        int round,
        JsonArray? lastToolResults,
        string? retryMessage)
    {
        var context = new JsonObject
        {
            ["userQuery"] = request.UserQuery,
            ["attemptNumber"] = request.AttemptNumber,
            ["repairRound"] = round,
            ["validationIssue"] = JsonSerializer.SerializeToNode(request.ValidationIssue, JsonOptions),
            ["workingPlan"] = session.GetCurrentPlanJson(),
            ["lastToolResults"] = lastToolResults?.DeepClone() ?? new JsonArray()
        };

        var prompt = $"Repair the invalid initial draft plan using the plan-editing tools.\n\nRepair context:\n{PlanningJson.SerializeNodeIndented(context)}";
        if (!string.IsNullOrWhiteSpace(retryMessage))
        {
            prompt += $"\n\nPrevious round issue:\n{retryMessage}\nContinue from the CURRENT working plan above. Do not restart from the original invalid draft.";
        }

        return prompt;
    }

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static string SerializeDetailedSummary(JsonNode? value) =>
        PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(value));

    private static PlanDefinition ClonePlan(PlanDefinition plan) =>
        JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
        ?? throw new InvalidOperationException("Failed to clone draft plan.");
}
