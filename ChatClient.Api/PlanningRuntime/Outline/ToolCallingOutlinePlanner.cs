using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Outline;

public sealed class ToolCallingOutlinePlanner(
    IChatClient chatClient,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : IOutlinePlanner
{
    private const int MaxRounds = 8;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    public async Task<PlanningStageResult<OutlinePlan>> CreatePlanAsync(
        OutlinePlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = new OutlineEditingSession();
        JsonArray? lastToolResults = null;
        string? retryMessage = null;
        var systemPrompt = BuildSystemPrompt();

        for (var round = 1; round <= MaxRounds; round++)
        {
            var runtime = new OutlineToolCallingRuntime(session, round, _log);
            var agent = new ChatClientAgent(
                chatClient,
                systemPrompt,
                "outline_planner",
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
                    throw new InvalidOperationException("Outline planner must use outline tools before replying.");

                _log.Log($"[outline] round={round} toolCalls={runtime.InvocationCount} completion={Shorten(completionText, 240)}");
                _log.Log($"[outline] round={round} toolResults={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(lastToolResults))}");

                var validation = session.ExecuteAction("outline.validate", new JsonObject());
                lastToolResults.Add(validation);
                _log.Log($"[outline] round={round} validation={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(validation))}");

                if (validation["ok"]?.GetValue<bool>() == true)
                {
                    return new PlanningStageResult<OutlinePlan>
                    {
                        Plan = session.BuildPlan(),
                        RawResponse = completionText
                    };
                }

                retryMessage = validation["error"]?["message"]?.GetValue<string>()
                    ?? "The outline plan is still invalid.";
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
                    "outline_planner",
                    $"Outline round {round} failed: {Shorten(ex.Message, 240)}"));
            }
            catch (Exception ex)
            {
                throw new PlanningContractException(
                    stage: "outline",
                    contractIssues: [$"tool-driven outline planner failed: {ex.Message}"],
                    rawResponse: ex.Message,
                    materializedJson: PlanningJson.SerializeNodeIndented(session.GetCurrentPlanJson()));
            }
        }

        throw new PlanningContractException(
            stage: "outline",
            contractIssues: ["tool-driven outline planner exhausted all rounds without a valid plan."],
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
        sb.AppendLine("You are the Outline Planner.");
        sb.AppendLine("Your job is to build a logical workflow, not an executable runtime plan.");
        sb.AppendLine("Build the outline only by calling the outline tools, then finish with one short plain-text note.");
        sb.AppendLine("Do not return JSON.");
        sb.AppendLine();
        sb.AppendLine("Required workflow:");
        sb.AppendLine("- Start by reading the current outline with outline_read_plan.");
        sb.AppendLine("- Set the goal with outline_set_goal.");
        sb.AppendLine("- Add requiredDeliverables when they matter for the final result.");
        sb.AppendLine("- Add, replace, link, and remove nodes only through outline tools.");
        sb.AppendLine("- Before finishing, call outline_validate.");
        sb.AppendLine("- If validation fails, fix the exact issue and validate again.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Identify the user-visible result artifact.");
        sb.AppendLine("- Build the shortest logically correct workflow that can produce that result.");
        sb.AppendLine("- Use only the listed capabilities and only at a logical level.");
        sb.AppendLine("- Separate external evidence acquisition from transformation and final answer generation.");
        sb.AppendLine("- Mark exactly one result node unless the outline is blocked.");
        sb.AppendLine("- Use a blocked plan only when the listed capabilities are insufficient.");
        sb.AppendLine("- Do not write raw step bindings, JSON paths, runtime input objects, output schemas, aggregate modes, or binding type hints.");
        sb.AppendLine("- Do not invent tools, agents, or hidden capabilities.");
        sb.AppendLine("- Do not optimize for a specific domain example.");
        sb.AppendLine("- Think in logical nodes such as discover, acquire, extract, filter, rank, synthesize, answer, act.");
        sb.AppendLine("- Each non-result node must feed a downstream node. The result node must be terminal.");
        sb.AppendLine("- Treat node kind as a hard execution contract for the later low-level stage.");
        sb.AppendLine("- discover means find candidate references only; do not pretend discovery already downloads or extracts content.");
        sb.AppendLine("- acquire means convert references/pages/urls into documents or page content.");
        sb.AppendLine("- extract, filter, rank, and synthesize are transform stages.");
        sb.AppendLine("- answer must be the terminal user-facing result node.");
        sb.AppendLine();
        sb.AppendLine("Allowed node kinds:");
        sb.AppendLine("- discover");
        sb.AppendLine("- acquire");
        sb.AppendLine("- extract");
        sb.AppendLine("- filter");
        sb.AppendLine("- rank");
        sb.AppendLine("- synthesize");
        sb.AppendLine("- answer");
        sb.AppendLine("- act");
        sb.AppendLine();
        sb.AppendLine("Node object shape for outline_add_node and outline_replace_node:");
        sb.AppendLine("{");
        sb.AppendLine("  \"id\": \"string\",");
        sb.AppendLine("  \"kind\": \"discover|acquire|extract|filter|rank|synthesize|answer|act\",");
        sb.AppendLine("  \"purpose\": \"string\",");
        sb.AppendLine("  \"dependsOn\": [\"nodeId\"],");
        sb.AppendLine("  \"inputs\": [{\"name\":\"string\",\"semanticType\":\"string\",\"fromNodeId\":\"nodeId\"}],");
        sb.AppendLine("  \"outputs\": [{\"name\":\"string\",\"semanticType\":\"string\"}],");
        sb.AppendLine("  \"constraints\": [\"string\"],");
        sb.AppendLine("  \"notes\": [\"string\"]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Finish with one short plain-text sentence like 'Outline ready.'");
        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        OutlinePlanningRequest request,
        OutlineEditingSession session,
        int round,
        JsonArray? lastToolResults,
        string? retryMessage)
    {
        var context = new JsonObject
        {
            ["userQuery"] = request.UserQuery,
            ["resultExpectations"] = request.ResultExpectations,
            ["capabilities"] = PlanningNodeJson.ToNode(request.Capabilities),
            ["outlineRound"] = round,
            ["workingPlan"] = session.GetCurrentPlanJson(),
            ["lastToolResults"] = lastToolResults?.DeepClone() ?? new JsonArray()
        };

        var prompt = $"Build the logical outline plan using the outline tools.\n\nOutline planning context:\n{PlanningJson.SerializeNodeIndented(context)}";
        if (!string.IsNullOrWhiteSpace(retryMessage))
            prompt += $"\n\nPrevious round issue:\n{retryMessage}\nContinue from the CURRENT outline plan above. Fix the exact contract/flow problem reported by validation rather than rephrasing the same outline.";

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
