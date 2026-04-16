using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class ToolCallingPlanningRequestAnalyzer(
    IChatClient chatClient,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : IPlanningRequestAnalyzer
{
    private const int MaxRounds = 6;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    public async Task<RequestBrief> AnalyzeAsync(
        string userQuery,
        CancellationToken cancellationToken = default)
    {
        _log.Log($"[plan] analyze:start query={Shorten(userQuery, 240)}");

        var session = new RequestBriefEditingSession();
        JsonArray? lastToolResults = null;
        string? retryMessage = null;
        var systemPrompt = BuildSystemPrompt();

        for (var round = 1; round <= MaxRounds; round++)
        {
            var runtime = new RequestBriefToolCallingRuntime(session, round, _log);
            var agent = new ChatClientAgent(
                chatClient,
                systemPrompt,
                "request_analyzer",
                null,
                runtime.CreateTools().ToList(),
                null,
                null);
            var roundPrompt = BuildRoundPrompt(userQuery, session, round, lastToolResults, retryMessage);

            try
            {
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis round {round}: building request brief with tools."));
                _observer.OnEvent(new AgentPromptPreparedEvent(
                    PlanningSpecialStepIds.Planning,
                    "request_analyzer",
                    systemPrompt,
                    roundPrompt,
                    roundPrompt,
                    CreateEmptyObject()));

                var completionText = await GenerateRoundCompletionAsync(agent, roundPrompt, cancellationToken);
                lastToolResults = runtime.GetInvocationResultsSnapshot();

                if (runtime.InvocationCount == 0)
                    throw new InvalidOperationException("Request analyzer must use request-brief tools before replying.");

                _log.Log($"[request-brief] round={round} toolCalls={runtime.InvocationCount} completion={Shorten(completionText, 240)}");
                _log.Log($"[request-brief] round={round} toolResults={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(lastToolResults))}");

                var validation = session.ExecuteAction("brief.validate", new JsonObject());
                lastToolResults.Add(validation);
                _log.Log($"[request-brief] round={round} validation={PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizeForLog(validation))}");

                if (validation["ok"]?.GetValue<bool>() == true)
                {
                    var brief = session.BuildBrief();
                    _observer.OnEvent(new AgentResponseReceivedEvent(
                        PlanningSpecialStepIds.Planning,
                        "request_analyzer",
                        completionText,
                        true,
                        JsonSerializer.SerializeToElement(brief),
                        null));
                    _observer.OnEvent(new RequestAnalysisCompletedEvent(brief));
                    _observer.OnEvent(new DiagnosticPlanRunEvent(
                        "planner",
                        $"Request analysis ready: deliverables={brief.Deliverables.Count}, outlineSteps={brief.SuggestedPlanOutline.Count}."));

                    _log.Log(
                        $"[plan] analyze:success deliverables={brief.Deliverables.Count} acquisitionNeeds={brief.AcquisitionNeeds.Count} reasoningNeeds={brief.ReasoningNeeds.Count} outlineSteps={brief.SuggestedPlanOutline.Count}");
                    _log.Log($"[plan] analyze:summary {PlanningJson.SerializeIndented(brief)}");
                    return brief;
                }

                retryMessage = validation["error"]?["message"]?.GetValue<string>()
                    ?? "The request brief is still invalid.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (round < MaxRounds)
            {
                lastToolResults = runtime.GetInvocationResultsSnapshot();
                retryMessage = ex.Message;
                _log.Log($"[plan] analyze:retry round={round} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis retry {round}: {Shorten(ex.Message, 240)}"));
                _observer.OnEvent(new AgentResponseReceivedEvent(
                    PlanningSpecialStepIds.Planning,
                    "request_analyzer",
                    retryMessage,
                    false,
                    null,
                    new ErrorInfo("request_analysis_invalid", ex.Message)));
            }
            catch (Exception ex)
            {
                _observer.OnEvent(new AgentResponseReceivedEvent(
                    PlanningSpecialStepIds.Planning,
                    "request_analyzer",
                    ex.Message,
                    false,
                    null,
                    new ErrorInfo("request_analysis_invalid", ex.Message)));
                throw new InvalidOperationException(
                    $"Request analyzer could not produce a valid planning analysis after {round} rounds. Last error: {ex.Message}",
                    ex);
            }
        }

        throw new InvalidOperationException($"Request analyzer could not produce a valid planning analysis after {MaxRounds} rounds.");
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
        sb.AppendLine("You are a request clarification agent for a planning runtime.");
        sb.AppendLine("You do NOT create the executable graph.");
        sb.AppendLine("You build the structured planning brief only through the request-brief tools, then finish with one short plain-text note.");
        sb.AppendLine("Do not return JSON.");
        sb.AppendLine();
        sb.AppendLine("Required workflow:");
        sb.AppendLine("- Start by reading the current brief with brief_read.");
        sb.AppendLine("- Fill the brief through brief_set_scalar, brief_append_list_item, and brief_replace_list.");
        sb.AppendLine("- Before finishing, call brief_validate.");
        sb.AppendLine("- If validation fails, fix the exact issue and validate again.");
        sb.AppendLine();
        sb.AppendLine("Scalar field names:");
        sb.AppendLine("- rewrittenRequest");
        sb.AppendLine("- goal");
        sb.AppendLine("- expectedResult");
        sb.AppendLine("- outputExpectations");
        sb.AppendLine();
        sb.AppendLine("List field names:");
        sb.AppendLine("- deliverables");
        sb.AppendLine("- constraints");
        sb.AppendLine("- acquisitionNeeds");
        sb.AppendLine("- evidenceRequirements");
        sb.AppendLine("- reasoningNeeds");
        sb.AppendLine("- successCriteria");
        sb.AppendLine("- ambiguityNotes");
        sb.AppendLine("- suggestedPlanOutline");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Correct obvious typos and normalize wording, but stay faithful to the original request.");
        sb.AppendLine("- Do not invent external facts, entities, tools, sources, or capabilities.");
        sb.AppendLine("- Do not add new user requirements that are not implied by the original request.");
        sb.AppendLine("- suggestedPlanOutline must stay coarse-grained and logical, not concrete tool calls.");
        sb.AppendLine("- acquisitionNeeds should contain only external information or external actions that must be obtained or performed.");
        sb.AppendLine("- evidenceRequirements should describe what evidence must be traceable in the final result.");
        sb.AppendLine("- reasoningNeeds should describe the comparisons, normalization, ranking, synthesis, or validation that happens after data is available.");
        sb.AppendLine("- deliverables should describe what the final answer or external outcome must contain.");
        sb.AppendLine("- expectedResult should be a short phrase for the result artifact type.");
        sb.AppendLine("- successCriteria should contain conditions the result must satisfy.");
        sb.AppendLine("- ambiguityNotes should list only real ambiguities.");
        sb.AppendLine("- outputExpectations should describe the requested presentation style.");
        sb.AppendLine("- Prefer short bullet-like strings inside list fields.");
        sb.AppendLine("- When the user request is clear, leave ambiguityNotes empty.");
        sb.AppendLine("- Finish with one short plain-text sentence like 'Request brief ready.'");
        return sb.ToString();
    }

    private static string BuildRoundPrompt(
        string userQuery,
        RequestBriefEditingSession session,
        int round,
        JsonArray? lastToolResults,
        string? retryMessage)
    {
        var context = new JsonObject
        {
            ["userQuery"] = userQuery,
            ["analysisRound"] = round,
            ["workingBrief"] = session.GetCurrentBriefJson(),
            ["lastToolResults"] = lastToolResults?.DeepClone() ?? new JsonArray()
        };

        var prompt = $"Build the planning brief for this user request using the request-brief tools.\n\nAnalysis context:\n{PlanningJson.SerializeNodeIndented(context)}";
        if (!string.IsNullOrWhiteSpace(retryMessage))
            prompt += $"\n\nPrevious round issue:\n{retryMessage}\nContinue from the CURRENT working brief above.";

        return prompt;
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
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
