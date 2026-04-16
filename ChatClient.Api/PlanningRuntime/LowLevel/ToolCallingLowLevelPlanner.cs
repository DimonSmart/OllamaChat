using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

public sealed class ToolCallingLowLevelPlanner(
    IChatClient chatClient,
    IReadOnlyCollection<AppToolDescriptor> tools,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : ILowLevelPlanner
{
    private const int MaxRounds = 10;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private readonly IReadOnlyCollection<AppToolDescriptor> _tools = tools;

    public async Task<PlanningStageResult<LowLevelPlan>> CreatePlanAsync(
        LowLevelPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = new LowLevelEditingSession(request.OutlinePlan);
        JsonArray? lastToolResults = null;
        string? retryMessage = null;
        var systemPrompt = BuildSystemPrompt();

        for (var round = 1; round <= MaxRounds; round++)
        {
            var runtime = new LowLevelToolCallingRuntime(session, _tools, round, _log);
            var agent = new ChatClientAgent(
                chatClient,
                systemPrompt,
                "low_level_planner",
                null,
                runtime.CreateTools().ToList(),
                null,
                null);
            var roundPrompt = BuildRoundPrompt(request, session, round, lastToolResults, retryMessage);

            try
            {
                var completionText = await GenerateRoundCompletionAsync(agent, roundPrompt, cancellationToken);
                lastToolResults = runtime.GetInvocationResultsSnapshot();

                if (runtime.InvocationCount == 0)
                    throw new InvalidOperationException("Low-level planner must use low-level tools before replying.");

                _log.Log($"[low-level] round={round} toolCalls={runtime.InvocationCount} completion={Shorten(completionText, 240)}");
                _log.Log($"[low-level] round={round} toolResults={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(lastToolResults))}");

                var validation = session.Validate(_tools);
                lastToolResults.Add(validation);
                _log.Log($"[low-level] round={round} validation={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(validation))}");

                if (validation["ok"]?.GetValue<bool>() == true)
                {
                    return new PlanningStageResult<LowLevelPlan>
                    {
                        Plan = session.BuildPlan(),
                        RawResponse = completionText
                    };
                }

                retryMessage = validation["error"]?["message"]?.GetValue<string>()
                    ?? "The low-level plan is still invalid.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (round < MaxRounds)
            {
                lastToolResults = runtime.GetInvocationResultsSnapshot();
                retryMessage = ex.Message;
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "low_level_planner",
                    $"Low-level round {round} failed: {Shorten(ex.Message, 240)}"));
            }
            catch (Exception ex)
            {
                throw new PlanningContractException(
                    stage: "low_level",
                    contractIssues: [$"tool-driven low-level planner failed: {ex.Message}"],
                    rawResponse: ex.Message,
                    materializedJson: PlanningJson.SerializeNodeIndented(session.GetCurrentPlanJson()));
            }
        }

        throw new PlanningContractException(
            stage: "low_level",
            contractIssues: ["tool-driven low-level planner exhausted all rounds without a valid plan."],
            rawResponse: string.Empty,
            materializedJson: PlanningJson.SerializeNodeIndented(session.GetCurrentPlanJson()));
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

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the Low-Level Planner.");
        sb.AppendLine("Your job is to convert an OutlinePlan into a concrete step graph, but not into a runtime IR.");
        sb.AppendLine("Build the low-level plan only by calling the low-level tools, then finish with one short plain-text note.");
        sb.AppendLine("Do not return JSON.");
        sb.AppendLine();
        sb.AppendLine("Required workflow:");
        sb.AppendLine("- Start by reading the current low-level plan with low_read_plan.");
        sb.AppendLine("- Add, replace, remove, and rewire steps only through low-level tools.");
        sb.AppendLine("- Mark the final step with low_mark_result_step unless the plan is blocked.");
        sb.AppendLine("- Before finishing, call low_validate.");
        sb.AppendLine("- If validation fails, fix the exact issue and validate again.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Preserve the logical meaning of the outline plan.");
        sb.AppendLine("- Choose exact capabilities from the provided catalog.");
        sb.AppendLine("- Outline node kind is a hard execution contract. Respect it when choosing capability kind and role.");
        sb.AppendLine("- discover nodes must use discover capabilities that produce references.");
        sb.AppendLine("- acquire nodes must use acquire capabilities that turn references into documents/content.");
        sb.AppendLine("- answer nodes must end in a terminal llm result step, not a tool step.");
        sb.AppendLine("- Define concrete steps, dependencies, semantic input/output ports, and fanout intent.");
        sb.AppendLine("- Keep the plan minimal and connected.");
        sb.AppendLine("- Do not write raw JSON paths, runtime binding objects, output schemas unless truly needed, binding type hints, or aggregate modes.");
        sb.AppendLine("- Use semantic ports and semantic types.");
        sb.AppendLine("- When a step should run once per item of an upstream collection, use source.mode='map' and fanout='per_item'.");
        sb.AppendLine("- If an output is a collection, or any downstream step will consume it with source.mode='map', its semanticType must explicitly end with []. Example: reference[] or document[].");
        sb.AppendLine("- Do not rely on runtime heuristics to infer collection shape from plural names alone.");
        sb.AppendLine("- If validation reports a tool-schema mismatch, fix the exact tool input name or binding required by that tool. Do not delete an evidence-gathering step just to silence the error.");
        sb.AppendLine("- If validation returns suggestedCapabilityIds, suggestedInputName, or suggestedBindingMode, apply one of those fixes unless you can produce another schema-valid equivalent.");
        sb.AppendLine("- Do not explain away an invalid binding. Replace the incompatible capability or binding.");
        sb.AppendLine("- Do not leave a non-terminal opaque json step after failed validation if a concrete schema-valid tool path exists.");
        sb.AppendLine("- All non-result steps must feed at least one downstream step. The result step must be terminal.");
        sb.AppendLine("- Tool steps use exact capability ids from the catalog. LLM steps use out.format.");
        sb.AppendLine();
        sb.AppendLine("Step object shape for low_add_step and low_replace_step:");
        sb.AppendLine("{");
        sb.AppendLine("  \"id\": \"string\",");
        sb.AppendLine("  \"outlineNodeId\": \"string\",");
        sb.AppendLine("  \"kind\": \"tool|llm\",");
        sb.AppendLine("  \"capabilityId\": \"string|null\",");
        sb.AppendLine("  \"purpose\": \"string\",");
        sb.AppendLine("  \"inputs\": [{\"name\":\"string\",\"source\":{\"kind\":\"literal|step_output_port\",\"value\":{},\"stepId\":\"string|null\",\"port\":\"string|null\",\"mode\":\"value|map|null\"}}],");
        sb.AppendLine("  \"outputs\": [{\"name\":\"string\",\"semanticType\":\"string\"}],");
        sb.AppendLine("  \"fanout\": \"single|per_item\",");
        sb.AppendLine("  \"out\": {\"format\":\"json|string\"},");
        sb.AppendLine("  \"isResult\": false");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Input source object shape for low_rewire_input:");
        sb.AppendLine("{");
        sb.AppendLine("  \"kind\": \"literal|step_output_port\",");
        sb.AppendLine("  \"value\": \"json|null\",");
        sb.AppendLine("  \"stepId\": \"string|null\",");
        sb.AppendLine("  \"port\": \"string|null\",");
        sb.AppendLine("  \"mode\": \"value|map|null\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Finish with one short plain-text sentence like 'Low-level plan ready.'");
        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        LowLevelPlanningRequest request,
        LowLevelEditingSession session,
        int round,
        JsonArray? lastToolResults,
        string? retryMessage)
    {
        var context = new JsonObject
        {
            ["outlinePlan"] = PlanningNodeJson.ToNode(request.OutlinePlan),
            ["capabilities"] = PlanningNodeJson.ToNode(request.Capabilities),
            ["lowLevelRound"] = round,
            ["workingPlan"] = session.GetCurrentPlanJson(),
            ["lastToolResults"] = lastToolResults?.DeepClone() ?? new JsonArray()
        };

        var prompt = $"Build the concrete low-level plan using the low-level tools.\n\nLow-level planning context:\n{PlanningJson.SerializeNodeIndented(context)}";
        if (!string.IsNullOrWhiteSpace(retryMessage))
            prompt += $"\n\nPrevious round issue:\n{retryMessage}\nContinue from the CURRENT low-level plan above. Read validation details from lastToolResults and fix the exact incompatible capability/binding.";

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
}
