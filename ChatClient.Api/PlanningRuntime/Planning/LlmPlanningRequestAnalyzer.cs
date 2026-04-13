using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class LlmPlanningRequestAnalyzer(
    IChatClient chatClient,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : IPlanningRequestAnalyzer
{
    private const int MaxAttempts = 2;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RequestBrief> AnalyzeAsync(
        string userQuery,
        CancellationToken cancellationToken = default)
    {
        _log.Log($"[plan] analyze:start query={Shorten(userQuery, 240)}");

        var systemPrompt = BuildSystemPrompt();
        var analysisPrompt = BuildUserPrompt(userQuery);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string? rawResponseText = null;

            try
            {
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis attempt {attempt}: preparing planning decomposition."));
                _observer.OnEvent(new AgentPromptPreparedEvent(
                    PlanningSpecialStepIds.Planning,
                    "request_analyzer",
                    systemPrompt,
                    analysisPrompt,
                    analysisPrompt,
                    CreateEmptyObject()));

                var stopwatch = Stopwatch.StartNew();
                var agent = new ChatClientAgent(chatClient, systemPrompt, "request_analyzer", null, null, null, null);
                var response = await agent.RunAsync<RequestBrief>(
                    analysisPrompt,
                    null,
                    JsonOptions,
                    null,
                    cancellationToken);
                rawResponseText = response.Text?.Trim() ?? string.Empty;
                stopwatch.Stop();

                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis attempt {attempt}: response received after {stopwatch.Elapsed:mm\\:ss} ({rawResponseText.Length} chars)."));

                var brief = response.Result
                    ?? throw new InvalidOperationException("Request analyzer returned an empty typed analysis result.");
                brief.ValidateOrThrow();

                _observer.OnEvent(new AgentResponseReceivedEvent(
                    PlanningSpecialStepIds.Planning,
                    "request_analyzer",
                    rawResponseText,
                    true,
                    JsonSerializer.SerializeToElement(brief),
                    null));

                _log.Log(
                    $"[plan] analyze:success deliverables={brief.Deliverables.Count} acquisitionNeeds={brief.AcquisitionNeeds.Count} reasoningNeeds={brief.ReasoningNeeds.Count} outlineSteps={brief.SuggestedPlanOutline.Count}");
                _log.Log($"[plan] analyze:summary {PlanningJson.SerializeIndented(brief)}");
                _observer.OnEvent(new RequestAnalysisCompletedEvent(brief));
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis ready: deliverables={brief.Deliverables.Count}, outlineSteps={brief.SuggestedPlanOutline.Count}."));
                return brief;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                if (!string.IsNullOrWhiteSpace(rawResponseText))
                {
                    _observer.OnEvent(new AgentResponseReceivedEvent(
                        PlanningSpecialStepIds.Planning,
                        "request_analyzer",
                        rawResponseText,
                        false,
                        null,
                        new ErrorInfo("request_analysis_invalid", ex.Message)));
                }

                lastError = ex;
                _log.Log($"[plan] analyze:retry attempt={attempt} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Request analysis retry {attempt}: {Shorten(ex.Message, 240)}"));
                analysisPrompt = BuildRepairPrompt(userQuery, ex, rawResponseText);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(rawResponseText))
                {
                    _observer.OnEvent(new AgentResponseReceivedEvent(
                        PlanningSpecialStepIds.Planning,
                        "request_analyzer",
                        rawResponseText,
                        false,
                        null,
                        new ErrorInfo("request_analysis_invalid", ex.Message)));
                }

                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Request analyzer could not produce a valid planning analysis after {MaxAttempts} attempts. Last error: {lastError?.Message}",
            lastError);
    }

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a request clarification agent for a planning runtime.");
        sb.AppendLine("You do NOT create the executable JSON plan.");
        sb.AppendLine("You rewrite the user request into a structured planning brief that captures intent, expected result, evidence needs, success criteria, and ambiguity.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Return ONLY one JSON object. No markdown fences. No prose.");
        sb.AppendLine("- Do not invent external facts, entities, tools, sources, or capabilities.");
        sb.AppendLine("- Do not add new user requirements that were not implied by the original request.");
        sb.AppendLine("- Keep the rewrite faithful to the original request, but make it clearer and easier to plan against.");
        sb.AppendLine("- suggestedPlanOutline must be coarse-grained logical steps, not concrete tool calls.");
        sb.AppendLine("- acquisitionNeeds should list only the external information or external actions that must be obtained or performed.");
        sb.AppendLine("- evidenceRequirements should list what evidence must be traceable in the final result (e.g. 'exact prices from product pages', 'cited publication dates'). Leave empty if the result does not require specific evidence.");
        sb.AppendLine("- reasoningNeeds should list transformations, comparisons, normalization, ranking, synthesis, or validation work that happens after data is available.");
        sb.AppendLine("- constraints should include explicit user constraints and important implied constraints like minimum item counts or output format when clearly requested.");
        sb.AppendLine("- deliverables should describe what the final answer or external outcome must contain.");
        sb.AppendLine("- expectedResult should be a short phrase describing the kind of artifact expected (e.g. 'ranked list', 'comparison table', 'factual summary', 'action confirmation'). Infer from the request.");
        sb.AppendLine("- successCriteria should list conditions the result must satisfy to be considered correct (e.g. 'at least 5 distinct items', 'all prices verified'). Leave empty if no specific criteria apply.");
        sb.AppendLine("- ambiguityNotes should list open questions or unclear parts of the request. Leave empty if the request is unambiguous.");
        sb.AppendLine("- outputExpectations should describe the format or presentation style expected (e.g. 'markdown table', 'bullet list', 'JSON'). Leave empty string if not specified.");
        sb.AppendLine("- Prefer short bullet-like strings inside arrays.");
        sb.AppendLine();
        sb.AppendLine("Return this exact JSON shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"rewrittenRequest\": \"string\",");
        sb.AppendLine("  \"goal\": \"string\",");
        sb.AppendLine("  \"expectedResult\": \"string\",");
        sb.AppendLine("  \"deliverables\": [\"string\"],");
        sb.AppendLine("  \"constraints\": [\"string\"],");
        sb.AppendLine("  \"acquisitionNeeds\": [\"string\"],");
        sb.AppendLine("  \"evidenceRequirements\": [\"string\"],");
        sb.AppendLine("  \"reasoningNeeds\": [\"string\"],");
        sb.AppendLine("  \"successCriteria\": [\"string\"],");
        sb.AppendLine("  \"ambiguityNotes\": [\"string\"],");
        sb.AppendLine("  \"outputExpectations\": \"string\",");
        sb.AppendLine("  \"suggestedPlanOutline\": [\"string\"]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildUserPrompt(string userQuery) =>
        $"Analyze this user request for planning.\n\nUser request:\n{userQuery}";

    private static string BuildRepairPrompt(string userQuery, Exception error, string? previousRawResponse)
    {
        var previousResponseBlock = string.IsNullOrWhiteSpace(previousRawResponse)
            ? string.Empty
            : $"\n\nPrevious invalid response:\n{previousRawResponse}";
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this user request for planning and correct your previous invalid response.");
        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(userQuery);
        sb.AppendLine();
        sb.AppendLine($"Validation error: {error.Message}");
        sb.Append(previousResponseBlock);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Return ONLY one corrected JSON object with the exact required fields.");
        return sb.ToString();
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
