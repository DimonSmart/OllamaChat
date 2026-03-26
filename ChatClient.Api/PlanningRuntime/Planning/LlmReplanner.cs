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

public sealed class LlmReplanner(
    IChatClient chatClient,
    PlanningToolCatalog toolCatalog,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null,
    PlanningCallableAgentCatalog? agentCatalog = null) : IReplanner
{
    private const int MaxRounds = 10;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private readonly PlanningCallableAgentCatalog _agentCatalog = agentCatalog ?? PlanningCallableAgentCatalog.Empty;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlanDefinition> ReplanAsync(PlannerReplanRequest request, CancellationToken cancellationToken = default)
    {
        var workflowTools = toolCatalog.ListTools();
        var session = new PlanEditingSession(request.Plan, PlanModelProfile.Runtime);
        var systemPrompt = BuildSystemPrompt(workflowTools);
        JsonArray? lastToolResults = null;
        string? retryMessage = null;

        _log.Log($"[replan] start attempt={request.AttemptNumber} reason={Shorten(request.GoalVerdict.Reason, 240)}");
        _observer.OnEvent(new ReplanStartedEvent(CloneRequest(request)));

        for (var round = 1; round <= MaxRounds; round++)
        {
            var roundStartSnapshot = SerializeWorkingPlan(session);
            var runtime = new PlanToolCallingRuntime(
                session,
                workflowTools,
                _agentCatalog,
                "replan",
                round,
                _log,
                stepId => ExecuteRuntimeReadFailedTrace(request, stepId));
            var agent = new ChatClientAgent(
                chatClient,
                systemPrompt,
                "replanner",
                null,
                runtime.CreateTools(includeRuntimeReadFailedTrace: true).ToList(),
                null,
                null);
            var roundPrompt = BuildRoundPrompt(request, session, round, lastToolResults, retryMessage);

            try
            {
                var completionText = await GenerateRoundCompletionAsync(agent, roundPrompt, cancellationToken);
                lastToolResults = runtime.GetInvocationResultsSnapshot();

                if (runtime.InvocationCount == 0)
                    throw new InvalidOperationException("Replanner must use at least one plan-editing or runtime inspection tool before replying.");

                _log.Log($"[replan] round={round} toolCalls={runtime.InvocationCount} completion={Shorten(completionText, 240)}");
                _log.Log($"[replan] round={round} toolResults={SerializeDetailedSummary(lastToolResults)}");

                var validationResult = PlanDraftValidationTool.CreateValidationResult(session, workflowTools, _agentCatalog);
                lastToolResults.Add(validationResult);
                _log.Log($"[replan] round={round} validation={SerializeDetailedSummary(validationResult)}");
                var hasEffectivePlanChange = !string.Equals(roundStartSnapshot, SerializeWorkingPlan(session), StringComparison.Ordinal);
                var roundCompleted = validationResult["ok"]?.GetValue<bool>() == true && hasEffectivePlanChange;
                var roundReason = validationResult["ok"]?.GetValue<bool>() == true && !hasEffectivePlanChange
                    ? "Replan made no effective changes to the working plan."
                    : completionText;
                _observer.OnEvent(new ReplanRoundCompletedEvent(
                    round,
                    roundCompleted,
                    roundReason,
                    JsonSerializer.SerializeToElement(new { completion = completionText }),
                    JsonSerializer.SerializeToElement(lastToolResults, JsonOptions)));

                if (validationResult["ok"]?.GetValue<bool>() == true)
                {
                    if (!hasEffectivePlanChange)
                    {
                        retryMessage = "The working draft is still unchanged. Apply a real repair with plan.replaceStep, plan.addSteps, plan.removeStep, or plan.resetFrom before finishing.";
                        _log.Log($"[replan] round={round} no-op");
                        continue;
                    }

                    var replanned = session.BuildPlan();
                    _log.Log($"[replan] success steps={replanned.Steps.Count} goal={Shorten(replanned.Goal, 240)}");
                    _log.Log($"[replan] summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(replanned))}");
                    _observer.OnEvent(new ReplanAppliedEvent(ClonePlan(replanned)));
                    return replanned;
                }

                retryMessage = validationResult["error"]?["message"]?.GetValue<string>()
                    ?? "The working draft is still invalid.";
            }
            catch (Exception ex) when (round < MaxRounds)
            {
                lastToolResults = runtime.GetInvocationResultsSnapshot();
                retryMessage = ex.Message;
                _log.Log($"[replan] round={round} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("replanner", $"Round {round} failed: {Shorten(ex.Message, 240)}"));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Replanner could not produce a valid plan after {round} rounds. Last error: {ex.Message}",
                    ex);
            }
        }

        throw new InvalidOperationException($"Replanner could not produce a valid plan after {MaxRounds} rounds.");
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
        sb.AppendLine("You are a replanning agent.");
        sb.AppendLine("You do NOT generate a full plan directly.");
        sb.AppendLine("You repair the working plan by using real plan-editing tools and runtime inspection tools.");
        sb.AppendLine("Use the tools directly, then finish with one short plain-text completion note in the form 'Diagnosis: ... Changes: ...'. Do not return JSON.");
        sb.AppendLine();
        sb.AppendLine("You MUST inspect or edit the working plan by calling tools. Do not describe edit actions in JSON.");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanReadStep}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanReplaceStep}(stepId, step)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanAddSteps}(afterStepId, steps)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanRemoveStep}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanResetFrom}(stepId)");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanValidateDraft}()");
        sb.AppendLine($"- {PlanningAgentToolNames.RuntimeReadFailedTrace}(stepId)");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use the existing working plan as the source of truth.");
        sb.AppendLine("- The working plan already contains per-step execution state in s/res/err.");
        sb.AppendLine("- Prefer the smallest correct repair.");
        sb.AppendLine("- First identify what is actually wrong in the CURRENT working draft, then repair that exact issue.");
        sb.AppendLine("- Reuse successful upstream steps whenever possible.");
        sb.AppendLine($"- The ONLY valid way to mutate the draft is by calling {PlanningAgentToolNames.PlanReplaceStep}, {PlanningAgentToolNames.PlanAddSteps}, or {PlanningAgentToolNames.PlanRemoveStep}.");
        sb.AppendLine($"- For {PlanningAgentToolNames.PlanReplaceStep}, the 'step' argument must be the FULL replacement plan step object, not a diff and not a summary.");
        sb.AppendLine("- Never paste previous tool outputs like before/after/diff/position back into a replacement step.");
        sb.AppendLine("- Do not repeat a failed extraction/comparison prompt unchanged when the failure data explains what was missing.");
        sb.AppendLine($"- Use {PlanningAgentToolNames.RuntimeReadFailedTrace}(stepId) when you need the compact structured details of a failed step.");
        sb.AppendLine($"- Use {PlanningAgentToolNames.PlanRemoveStep}(stepId) to delete dead passthrough steps, duplicate finalizers, stale placeholder steps, or branches that no longer belong in the repaired plan.");
        sb.AppendLine("- Do not add, estimate, normalize, or impute exact factual values that are missing from executed evidence.");
        sb.AppendLine("- When the user request needs precise facts and those facts are missing, repair the plan by retrieving better evidence, narrowing scope, or preserving a blocked/missing contract.");
        sb.AppendLine("- External capabilities return raw references, documents, records, or action results. They do not automatically produce verified final answers.");
        sb.AppendLine("- If the failure indicates ambiguity, duplicates, candidate sets, or missing normalization, repair the plan shape by adding explicit select/normalize/verify steps instead of only rewriting the final prompt.");
        sb.AppendLine("- If final verification says the answer is missing required deliverables, repair the upstream plan so those deliverables are explicitly gathered and preserved.");
        sb.AppendLine("- Do not add new user requirements during repair. If the failed plan demanded stricter evidence than the user asked for, repair that unnecessary restriction instead of escalating scope.");
        sb.AppendLine("- If the current capabilities cannot obtain or verify a required deliverable, stop trying to force a success answer. Repair the plan into a short blocked result with code 'insufficient_capabilities', details.status='blocked', details.needsReplan=false, and details.type='insufficient_capability'.");
        sb.AppendLine("- Do not weaken an upstream field from required/non-null to nullable if a downstream tool input still requires a non-null value. Repair the evidence plan instead.");
        sb.AppendLine("- If the failed trace has type='missing' and your edit removes that requirement or makes it optional, stop iterating and finish with a short completion note.");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanResetFrom}(stepId) resets execution state for that step and all downstream steps so the executor will rerun them.");
        sb.AppendLine($"- {PlanningAgentToolNames.PlanValidateDraft}() returns whether the current working draft passes structural validation.");
        sb.AppendLine("- Do not finish with generic text like 'Replan repaired.'. Your final note must briefly name the diagnosed problem and the concrete edit you made.");
        sb.AppendLine("- When you are confident the working draft is ready, return one short plain-text sentence like 'Diagnosis: unused terminal passthrough steps kept the plan invalid. Changes: removed the extra formatter steps and kept the real final output path.'");
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
        sb.AppendLine("- For LLM or saved-agent steps, when input shape matters, add inline field 'type' inside the binding object. Example: {\"from\":\"$step1.items\",\"mode\":\"value\",\"type\":\"array<object>\"}.");
        sb.AppendLine("- The binding field 'type' describes one resolved call input. Example: mode='value' with an array ref usually means type='array<object>', while mode='map' with the same ref usually means type='object'.");
        sb.AppendLine("- Supported binding types are: string, number, integer, boolean, object, array, array<string>, array<number>, array<integer>, array<boolean>, array<object>.");
        sb.AppendLine("- binding.mode supports ONLY 'value' or 'map'. Never use 'flatten' as a binding mode.");
        sb.AppendLine("- Invalid example: {\"from\":\"$s1.items[]\",\"mode\":\"flatten\"}.");
        sb.AppendLine("- Literal tool inputs must be plain JSON literals. Never wrap them in helper objects like {\"value\":...}.");
        sb.AppendLine("- mode='map' means run the step once per array element.");
        sb.AppendLine("- Use concat only to merge multiple array sources into one array input. Every concat item must resolve to an array and must use mode='value'.");
        sb.AppendLine("- If a tool returns an object containing an array field, bind from that field directly.");
        sb.AppendLine("- If a downstream tool needs a projected array field, express it explicitly in the ref.");
        sb.AppendLine("- When a capability should process one record at a time, prefer a per-item binding with mode='map'.");
        sb.AppendLine("- If one input may contain multiple candidate entities but a downstream step expects one, add an explicit select/target input instead of hoping the prompt guesses correctly.");
        sb.AppendLine("- Tool steps must not declare out. The runtime derives tool output contracts from the tool catalog.");
        sb.AppendLine("- LLM and saved-agent steps must declare out.format and when out.format='json', out.schema.");
        sb.AppendLine("- Every schema node must declare either type or enum.");
        sb.AppendLine("- If out.schema.type='object', every entry inside out.schema.properties must itself declare type or enum.");
        sb.AppendLine("- If out.schema.type='array', out.schema.items must declare type or enum.");
        sb.AppendLine("- If out.format='string', either omit out.schema or use out.schema={\"type\":\"string\"}.");
        sb.AppendLine("- If the prompt allows missing fields to become null, reflect that in the schema with nullable=true or type=[\"<base>\",\"null\"].");
        sb.AppendLine("- Every non-final step must feed at least one downstream consumer. The only terminal step must be the last step.");
        sb.AppendLine("- 'flatten' is allowed only in out.aggregate, never in binding.mode.");
        sb.AppendLine("- Omit out.aggregate unless a validation issue specifically requires changing it. The runtime infers 'single', 'collect', or 'flatten' from mapped inputs and schema.");
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendTools(sb, workflowTools);
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendAgents(sb, _agentCatalog.ListAgents());

        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        PlannerReplanRequest request,
        PlanEditingSession session,
        int round,
        JsonArray? lastToolResults,
        string? retryMessage)
    {
        var context = new JsonObject
        {
            ["userQuery"] = request.UserQuery,
            ["attemptNumber"] = request.AttemptNumber,
            ["replanRound"] = round,
            ["goalVerdict"] = JsonSerializer.SerializeToNode(request.GoalVerdict, JsonOptions),
            ["executionSummary"] = BuildExecutionSummary(request.ExecutionResult),
            ["failedTraceHints"] = BuildFailedTraceHints(request.ExecutionResult),
            ["workingPlan"] = session.GetCurrentPlanJson(),
            ["lastToolResults"] = lastToolResults?.DeepClone() ?? new JsonArray()
        };

        var prompt = $"Repair the working plan using the plan-editing tools.\n\nReplanning context:\n{PlanningJson.SerializeNodeIndented(context)}";
        if (!string.IsNullOrWhiteSpace(retryMessage))
        {
            prompt += $"\n\nPrevious round issue:\n{retryMessage}\nContinue from the CURRENT working plan above. Do not restart from the original failed draft.";
        }

        return prompt;
    }

    private static JsonObject ExecuteRuntimeReadFailedTrace(PlannerReplanRequest request, string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            return CreateToolFailure("tool_error", "Action input 'stepId' is required.", "runtime.readFailedTrace");

        var failedTrace = request.ExecutionResult.StepTraces
            .FirstOrDefault(trace =>
                string.Equals(trace.StepId, stepId, StringComparison.Ordinal)
                && trace.Outcome == StepTraceOutcome.Failed);
        if (failedTrace is null)
        {
            return CreateToolFailure(
                "tool_error",
                $"Failed trace for step '{stepId}' was not found.",
                "runtime.readFailedTrace");
        }

        return new JsonObject
        {
            ["tool"] = "runtime.readFailedTrace",
            ["ok"] = true,
            ["output"] = BuildFailedTraceSummary(failedTrace)
        };
    }

    private static JsonArray BuildExecutionSummary(ExecutionResult executionResult)
    {
        var summary = new JsonArray();
        foreach (var trace in executionResult.StepTraces)
        {
            summary.Add(new JsonObject
            {
                ["stepId"] = trace.StepId,
                ["outcome"] = trace.Outcome.ToString().ToLowerInvariant(),
                ["errorCode"] = trace.ErrorCode,
                ["verificationIssues"] = new JsonArray(trace.VerificationIssues.Select(issue => JsonValue.Create(issue.Code)).ToArray())
            });
        }

        return summary;
    }

    private static JsonArray BuildFailedTraceHints(ExecutionResult executionResult)
    {
        var failedTraces = new JsonArray();
        foreach (var trace in executionResult.StepTraces.Where(trace => trace.Outcome == StepTraceOutcome.Failed))
            failedTraces.Add(BuildFailedTraceSummary(trace));

        return failedTraces;
    }

    private static JsonObject BuildFailedTraceSummary(StepExecutionTrace failedTrace)
    {
        JsonElement? status = null;
        JsonElement? needsReplan = null;
        JsonElement? type = null;
        var details = new HashSet<string>(StringComparer.Ordinal);

        if (failedTrace.ErrorDetails is { ValueKind: JsonValueKind.Object } errorDetails)
        {
            if (errorDetails.TryGetProperty("status", out var statusElement))
                status = statusElement.Clone();

            if (errorDetails.TryGetProperty("needsReplan", out var needsReplanElement))
                needsReplan = needsReplanElement.Clone();

            if (errorDetails.TryGetProperty("type", out var typeElement))
                type = typeElement.Clone();

            if (errorDetails.TryGetProperty("details", out var detailItems) && detailItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var detailNode in detailItems.EnumerateArray())
                {
                    if (detailNode.ValueKind != JsonValueKind.String)
                        continue;

                    var detail = detailNode.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(detail))
                        details.Add(detail);
                }
            }
        }

        return new JsonObject
        {
            ["stepId"] = failedTrace.StepId,
            ["errorCode"] = failedTrace.ErrorCode,
            ["errorMessage"] = failedTrace.ErrorMessage,
            ["status"] = SerializeElementToNode(status),
            ["needsReplan"] = SerializeElementToNode(needsReplan),
            ["type"] = SerializeElementToNode(type),
            ["details"] = new JsonArray(details.Select(detail => JsonValue.Create(detail)).ToArray())
        };
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
        element is null ? null : JsonSerializer.SerializeToNode(element.Value);

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

    private static string SerializeWorkingPlan(PlanEditingSession session) =>
        session.GetCurrentPlanJson().ToJsonString(PlanningJson.CompactOptions);

    private static PlanDefinition ClonePlan(PlanDefinition plan) =>
        JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
        ?? throw new InvalidOperationException("Failed to clone replanned plan.");

    private static PlannerReplanRequest CloneRequest(PlannerReplanRequest request) =>
        new()
        {
            UserQuery = request.UserQuery,
            AttemptNumber = request.AttemptNumber,
            Plan = ClonePlan(request.Plan),
            ExecutionResult = request.ExecutionResult,
            GoalVerdict = request.GoalVerdict
        };

}
