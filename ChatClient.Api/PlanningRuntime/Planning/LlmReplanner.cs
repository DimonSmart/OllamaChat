using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class LlmReplanner(
    IChatClient chatClient,
    PlanningToolCatalog toolCatalog,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : IReplanner
{
    private const int MaxRounds = 6;
    private const int MaxResponseAttempts = 3;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlanDefinition> ReplanAsync(PlannerReplanRequest request, CancellationToken cancellationToken = default)
    {
        var workflowTools = toolCatalog.ListTools();
        var session = new PlanEditingSession(request.Plan);
        var systemPrompt = BuildSystemPrompt(workflowTools);
        var agent = new ChatClientAgent(chatClient, systemPrompt, "replanner", null, null, null, null);
        JsonArray? lastActionResults = null;

        _log.Log($"[replan] start attempt={request.AttemptNumber} reason={Shorten(request.GoalVerdict.Reason, 240)}");
        _observer.OnEvent(new ReplanStartedEvent(CloneRequest(request)));

        for (var round = 1; round <= MaxRounds; round++)
        {
            var roundPrompt = BuildRoundPrompt(request, session, round, lastActionResults);
            var actionBatch = await GenerateActionBatchAsync(agent, roundPrompt, cancellationToken);
            var done = actionBatch.Done;
            var reason = actionBatch.Reason.Trim();

            _log.Log($"[replan] round={round} done={done} actions={actionBatch.Actions.Count} reason={Shorten(reason, 240)}");
            _log.Log($"[replan] round={round} actionBatch={PlanningJson.SerializeIndented(actionBatch)}");

            lastActionResults = ExecuteActions(session, request, actionBatch.Actions);
            _log.Log($"[replan] round={round} actionResults={PlanningJson.SerializeNodeIndented(lastActionResults)}");
            _observer.OnEvent(new ReplanRoundCompletedEvent(
                round,
                done,
                reason,
                JsonSerializer.SerializeToElement(actionBatch, JsonOptions),
                JsonSerializer.SerializeToElement(lastActionResults, JsonOptions)));

            if (!done)
                continue;

            try
            {
                var replanned = session.BuildPlan();
                PlanValidator.ValidateOrThrow(replanned, workflowTools);
                _log.Log($"[replan] success steps={replanned.Steps.Count} goal={Shorten(replanned.Goal, 240)}");
                _log.Log($"[replan] json {PlanningJson.SerializeIndented(replanned)}");
                _observer.OnEvent(new ReplanAppliedEvent(ClonePlan(replanned)));
                return replanned;
            }
            catch (Exception ex) when (round < MaxRounds)
            {
                lastActionResults.Add(new JsonObject
                {
                    ["tool"] = "plan.validateDraft",
                    ["ok"] = false,
                    ["error"] = new JsonObject
                    {
                        ["code"] = "invalid_plan",
                        ["message"] = ex.Message
                    }
                });
            }
        }

        throw new InvalidOperationException($"Replanner could not produce a valid plan after {MaxRounds} rounds.");
    }

    private async Task<ReplanActionBatch> GenerateActionBatchAsync(
        ChatClientAgent agent,
        string roundPrompt,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var currentPrompt = roundPrompt;

        for (var attempt = 1; attempt <= MaxResponseAttempts; attempt++)
        {
            try
            {
                var response = await agent.RunAsync<ResultEnvelope<ReplanActionBatch>>(currentPrompt, null, JsonOptions, null, cancellationToken);
                var envelope = response.Result
                    ?? throw new InvalidOperationException("Replanner returned an empty response envelope.");
                var actionBatch = envelope.GetRequiredDataOrThrow("Replanner");
                ValidateActionBatch(actionBatch);
                return actionBatch;
            }
            catch (Exception ex) when (attempt < MaxResponseAttempts)
            {
                lastError = ex;
                _log.Log($"[replan] response:retry attempt={attempt} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("replanner", $"Response retry {attempt}: {Shorten(ex.Message, 240)}"));
                currentPrompt = BuildRepairPrompt(roundPrompt, ex.Message);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Replanner did not return a valid action batch after {MaxResponseAttempts} attempts. Last error: {lastError?.Message}",
            lastError);
    }

    private static void ValidateActionBatch(ReplanActionBatch actionBatch)
    {
        if (!actionBatch.Done && actionBatch.Actions.Count == 0)
            throw new InvalidOperationException("Replanner must return at least one action when 'done' is false.");

        foreach (var action in actionBatch.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Tool))
                throw new InvalidOperationException("Each action must include 'tool'.");
        }
    }

    private static string BuildSystemPrompt(IReadOnlyCollection<AppToolDescriptor> workflowTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a replanning agent.");
        sb.AppendLine("You do NOT generate a full plan directly.");
        sb.AppendLine("You repair the working plan by calling plan-editing tools.");
        sb.AppendLine("Return ONLY JSON. No markdown. No prose outside the JSON.");
        sb.AppendLine();
        sb.AppendLine("Your response must use this exact top-level shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"ok\": true|false,");
        sb.AppendLine("  \"data\": <action-batch|null>,");
        sb.AppendLine("  \"error\": null|{");
        sb.AppendLine("    \"code\": \"string\",");
        sb.AppendLine("    \"message\": \"string\",");
        sb.AppendLine("    \"details\": { }|null");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("When replanning succeeds, return ok=true, error=null, and put the action batch into data.");
        sb.AppendLine("When replanning fails, return ok=false, data=null, and put the failure reason into error.");
        sb.AppendLine();
        sb.AppendLine("The action batch inside data must have this exact shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"done\": true|false,");
        sb.AppendLine("  \"reason\": \"short explanation\",");
        sb.AppendLine("  \"actions\": [");
        sb.AppendLine("    { \"tool\": \"plan.readStep|plan.replaceStep|plan.addSteps|plan.resetFrom|runtime.readFailedTrace\", \"in\": { ... } }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use the existing working plan as the source of truth.");
        sb.AppendLine("- The working plan already contains per-step execution state in s/res/err.");
        sb.AppendLine("- Prefer the smallest correct repair.");
        sb.AppendLine("- Reuse successful upstream steps whenever possible.");
        sb.AppendLine("- Do not repeat a failed extraction/comparison prompt unchanged when the failure data explains what was missing.");
        sb.AppendLine("- Use runtime.readFailedTrace(stepId) when you need the compact structured details of a failed step.");
        sb.AppendLine("- Do not add, estimate, normalize, or impute exact factual values that are missing from executed evidence.");
        sb.AppendLine("- When the user request needs precise facts and those facts are missing, repair the plan by retrieving better evidence, narrowing scope, or preserving a blocked/missing contract.");
        sb.AppendLine("- If the failed trace has type='missing' and your edit removes that requirement or makes it optional, finish with done=true instead of iterating further.");
        sb.AppendLine("- plan.resetFrom(stepId) resets execution state for that step and all downstream steps so the executor will rerun them.");
        sb.AppendLine("- plan.replaceStep(stepId, step) replaces exactly one existing step in place.");
        sb.AppendLine("- plan.addSteps(afterStepId, steps) inserts new steps after the specified step. Use afterStepId=null only when rebuilding from an empty draft.");
        sb.AppendLine("- plan.readStep(stepId) returns the current JSON of one step.");
        sb.AppendLine("- When you are confident the working draft is ready, set done=true. You may include final edit actions in the same response.");
        sb.AppendLine();
        sb.AppendLine("Plan step rules:");
        sb.AppendLine("- A step must have exactly one of 'tool' or 'llm'.");
        sb.AppendLine("- LLM steps must have systemPrompt and userPrompt.");
        sb.AppendLine("- Tool steps must use the exact workflow tool name listed below, including any server prefix.");
        sb.AppendLine("- Put dynamic inputs under 'in' using binding objects like {\"from\":\"$step.ref\",\"mode\":\"value|map\"}.");
        sb.AppendLine("- mode='map' means run the step once per array element.");
        sb.AppendLine("- If a tool returns an object containing an array field, bind from that field directly (for example, {\"from\":\"$search.results\",\"mode\":\"map\"}).");
        sb.AppendLine("- If a downstream tool needs a projected array field, express it explicitly in the ref (for example, {\"from\":\"$search.results[].url\",\"mode\":\"map\"}).");
        sb.AppendLine("- When chaining search-style results into download-style tools, prefer binding the whole array field into input key 'page' so download preserves metadata and adds content.");
        sb.AppendLine("- Download-style tools may return the original page object enriched with 'content'.");
        sb.AppendLine("- For download-style tools, pass exactly one of 'page' or 'url'. Do not send both.");
        sb.AppendLine("- For extraction tasks, prefer a per-item binding with mode='map'.");
        sb.AppendLine("- LLM steps must declare out.format, out.aggregate, and when out.format='json', out.schema.");
        sb.AppendLine("- Every schema node must declare either type or enum.");
        sb.AppendLine("- If out.schema.type='object', every entry inside out.schema.properties must itself declare type or enum.");
        sb.AppendLine("- If out.schema.type='array', out.schema.items must declare type or enum.");
        sb.AppendLine("- If out.format='string', either omit out.schema or use out.schema={\"type\":\"string\"}.");
        sb.AppendLine("- If the prompt allows missing fields to become null, reflect that in the schema with nullable=true or type=[\"<base>\",\"null\"].");
        sb.AppendLine("- Use out.aggregate='collect' for mapped single-item extraction and out.aggregate='flatten' for mapped multi-item extraction.");
        sb.AppendLine("- Use out.aggregate='single' when the step has no mapped inputs.");
        sb.AppendLine("- If out.aggregate='collect', out.schema must describe one call result item, not the final collected array.");
        sb.AppendLine("- If out.aggregate='flatten', out.schema must describe the per-call array shape that will be flattened.");
        sb.AppendLine();
        sb.AppendLine("Available workflow tools:");
        foreach (var tool in workflowTools)
        {
            sb.AppendLine($"- name: {tool.QualifiedName}");
            sb.AppendLine($"  description: {tool.Description}");
            sb.AppendLine($"  inputSchema: {PlanningJson.SerializeElementCompact(tool.InputSchema)}");
            sb.AppendLine($"  outputSchema: {PlanningJson.SerializeElementCompact(tool.OutputSchema)}");
        }

        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        PlannerReplanRequest request,
        PlanEditingSession session,
        int round,
        JsonArray? lastActionResults)
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
            ["lastActionResults"] = lastActionResults?.DeepClone() ?? new JsonArray()
        };

        return $"Repair the working plan using the plan-editing tools.\n\nReplanning context:\n{PlanningJson.SerializeNodeIndented(context)}";
    }

    private static string BuildRepairPrompt(string originalPrompt, string errorMessage) =>
        $"{originalPrompt}\n\nYour previous response was invalid.\nValidation error: {errorMessage}\n\nReturn a corrected ResultEnvelope<ReplanActionBatch> as JSON only and follow the exact schema.";

    private static JsonArray ExecuteActions(
        PlanEditingSession session,
        PlannerReplanRequest request,
        IReadOnlyCollection<ReplanAction> actions)
    {
        var results = new JsonArray();

        foreach (var action in actions)
        {
            results.Add(string.Equals(action.Tool, "runtime.readFailedTrace", StringComparison.Ordinal)
                ? ExecuteRuntimeReadFailedTrace(request, action)
                : session.ExecuteAction(action.Tool, action.Input));
        }

        return results;
    }

    private static JsonObject ExecuteRuntimeReadFailedTrace(PlannerReplanRequest request, ReplanAction action)
    {
        var stepId = action.Input["stepId"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(stepId))
            return CreateToolFailure("tool_error", "Action input 'stepId' is required.", "runtime.readFailedTrace");

        var failedTrace = request.ExecutionResult.StepTraces
            .FirstOrDefault(trace => string.Equals(trace.StepId, stepId, StringComparison.Ordinal) && !trace.Success);
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
                ["success"] = trace.Success,
                ["errorCode"] = trace.ErrorCode,
                ["verificationIssues"] = new JsonArray(trace.VerificationIssues.Select(issue => JsonValue.Create(issue.Code)).ToArray())
            });
        }

        return summary;
    }

    private static JsonArray BuildFailedTraceHints(ExecutionResult executionResult)
    {
        var failedTraces = new JsonArray();
        foreach (var trace in executionResult.StepTraces.Where(trace => !trace.Success))
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

    private sealed class ReplanActionBatch
    {
        [JsonRequired]
        [JsonPropertyName("done")]
        public bool Done { get; init; }

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("actions")]
        public List<ReplanAction> Actions { get; init; } = [];
    }

    private sealed class ReplanAction
    {
        [JsonRequired]
        [JsonPropertyName("tool")]
        public string Tool { get; init; } = string.Empty;

        [JsonPropertyName("in")]
        public JsonObject Input { get; init; } = [];
    }
}
