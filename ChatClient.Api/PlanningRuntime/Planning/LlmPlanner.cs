using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Generates a PlanDefinition from a natural-language user query by asking the LLM.
/// The planner returns the canonical plan model through a draft serialization profile,
/// then validation and runtime hydration happen outside of the model type itself.
/// </summary>
public sealed class LlmPlanner(
    IChatClient chatClient,
    PlanningToolCatalog toolCatalog,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null,
    IInitialDraftRepairer? initialDraftRepairer = null,
    PlanningCallableAgentCatalog? agentCatalog = null) : IPlanner
{
    private const int MaxDraftGenerations = 2;
    private const string PlannerStepId = "__planning__";
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private readonly IInitialDraftRepairer? _initialDraftRepairer = initialDraftRepairer;
    private readonly PlanningCallableAgentCatalog _agentCatalog = agentCatalog ?? PlanningCallableAgentCatalog.Empty;

    public async Task<PlanDefinition> CreatePlanAsync(string userQuery, CancellationToken cancellationToken = default)
    {
        _log.Log($"[plan] create:start toolCount={toolCatalog.ListTools().Count} query={Shorten(userQuery, 240)}");
        return await GeneratePlanCoreAsync(BuildPlanningUserPrompt(userQuery), cancellationToken);
    }

    private async Task<PlanDefinition> GeneratePlanCoreAsync(
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var tools = toolCatalog.ListTools();
        var systemPrompt = BuildSystemPrompt(tools);
        var agent = new ChatClientAgent(chatClient, systemPrompt, "planner", null, null, null, null);
        var planningPrompt = userPrompt;
        Exception? lastError = null;
        string? previousDraftJson = null;

        for (var attempt = 1; attempt <= MaxDraftGenerations; attempt++)
        {
            string? rawResponseText = null;

            try
            {
                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Attempt {attempt}: requesting draft (systemChars={systemPrompt.Length}, userChars={planningPrompt.Length}, tools={tools.Count})."));
                _observer.OnEvent(new AgentPromptPreparedEvent(
                    PlannerStepId,
                    "planner",
                    systemPrompt,
                    planningPrompt,
                    planningPrompt,
                    CreateEmptyObject()));

                var stopwatch = Stopwatch.StartNew();
                var response = await agent.RunAsync<PlanDefinition>(
                    planningPrompt,
                    null,
                    PlanJsonProfiles.DraftCompactOptions,
                    null,
                    cancellationToken);
                rawResponseText = response.Text?.Trim() ?? string.Empty;
                stopwatch.Stop();

                _observer.OnEvent(new DiagnosticPlanRunEvent(
                    "planner",
                    $"Attempt {attempt}: response received after {stopwatch.Elapsed:mm\\:ss} ({rawResponseText.Length} chars)."));

                var plan = response.Result
                    ?? throw new InvalidOperationException("Planner returned an empty typed plan result.");
                PlanSanitizer.Sanitize(plan, PlanModelProfile.Draft);

                _observer.OnEvent(new AgentResponseReceivedEvent(
                    PlannerStepId,
                    "planner",
                    rawResponseText,
                    true,
                    PlanJsonProfiles.SerializeToElement(plan, PlanModelProfile.Draft),
                    null));

                previousDraftJson = PlanJsonProfiles.SerializeIndented(plan, PlanModelProfile.Draft);
                if (!PlanValidator.TryValidate(plan, tools, _agentCatalog.ListAgents(), PlanModelProfile.Draft, out var validationIssue))
                {
                    var validationException = new PlanValidationException(validationIssue!);
                    _log.Log($"[plan] create:invalid attempt={attempt} error={Shorten(validationException.Message, 240)} issue={PlanningJson.SerializeCompact(validationIssue)}");
                    _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Retry {attempt}: {Shorten(validationException.Message, 240)}"));

                    if (_initialDraftRepairer is not null && attempt == 1)
                    {
                        try
                        {
                            _log.Log($"[plan] create:repair attempt={attempt} issue={PlanningJson.SerializeCompact(validationIssue)}");
                            var repaired = await _initialDraftRepairer.RepairAsync(new InitialDraftRepairRequest
                            {
                                UserQuery = userPrompt,
                                AttemptNumber = attempt,
                                DraftPlan = ClonePlan(plan, PlanModelProfile.Draft),
                                ValidationIssue = validationIssue!
                            }, cancellationToken);

                            PlanSanitizer.Sanitize(repaired, PlanModelProfile.Draft);
                            if (!PlanValidator.TryValidate(repaired, tools, _agentCatalog.ListAgents(), PlanModelProfile.Draft, out var repairedValidationIssue))
                                throw new PlanValidationException(repairedValidationIssue!);

                            _log.Log($"[plan] create:success steps={repaired.Steps.Count} goal={Shorten(repaired.Goal, 240)}");
                            _log.Log($"[plan] create:summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(repaired, PlanModelProfile.Draft))}");
                            _observer.OnEvent(new PlanCreatedEvent(attempt, "plan", ClonePlan(repaired, PlanModelProfile.Draft)));
                            return repaired;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception repairEx) when (attempt < MaxDraftGenerations)
                        {
                            lastError = repairEx;
                            _log.Log($"[plan] create:repair-failed attempt={attempt} error={Shorten(repairEx.Message, 240)}");
                            _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Repair {attempt} failed: {Shorten(repairEx.Message, 240)}"));
                            planningPrompt = BuildRepairPrompt(userPrompt, repairEx, previousDraftJson);
                            continue;
                        }
                        catch (Exception repairEx)
                        {
                            lastError = repairEx;
                            break;
                        }
                    }

                    if (attempt < MaxDraftGenerations)
                    {
                        lastError = validationException;
                        _log.Log($"[plan] create:fallback attempt={attempt + 1} reason={Shorten(validationException.Message, 240)}");
                        _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Fallback {attempt + 1}: {Shorten(validationException.Message, 240)}"));
                        planningPrompt = BuildRepairPrompt(userPrompt, validationException, previousDraftJson);
                        continue;
                    }

                    lastError = validationException;
                    break;
                }

                _log.Log($"[plan] create:success steps={plan.Steps.Count} goal={Shorten(plan.Goal, 240)}");
                _log.Log($"[plan] create:summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(plan, PlanModelProfile.Draft))}");
                _observer.OnEvent(new PlanCreatedEvent(attempt, "plan", ClonePlan(plan, PlanModelProfile.Draft)));
                return plan;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxDraftGenerations)
            {
                if (!string.IsNullOrWhiteSpace(rawResponseText))
                {
                    _observer.OnEvent(new AgentResponseReceivedEvent(
                        PlannerStepId,
                        "planner",
                        rawResponseText,
                        false,
                        null,
                        new ErrorInfo("planner_invalid_contract", ex.Message)));
                }

                lastError = ex;
                _log.Log($"[plan] create:retry attempt={attempt} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Retry {attempt}: {Shorten(ex.Message, 240)}"));
                planningPrompt = BuildRepairPrompt(userPrompt, ex, previousDraftJson);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(rawResponseText))
                {
                    _observer.OnEvent(new AgentResponseReceivedEvent(
                        PlannerStepId,
                        "planner",
                        rawResponseText,
                        false,
                        null,
                        new ErrorInfo("planner_invalid_contract", ex.Message)));
                }

                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid plan after {MaxDraftGenerations} draft generations. Last error: {lastError?.Message}",
            lastError);
    }

    private string BuildSystemPrompt(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a planning agent.");
        sb.AppendLine("Build the shortest correct plan that satisfies the user request.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Return one JSON plan object with top-level goal and steps.");
        sb.AppendLine("- Use only the tools and saved agents listed below. Never invent capabilities.");
        sb.AppendLine("- Every step must include id, kind, name, and in.");
        sb.AppendLine("- kind must be exactly one of 'tool', 'llm', or 'agent'.");
        sb.AppendLine("- name must be the exact tool name, llm label, or saved-agent id for the selected kind.");
        sb.AppendLine("- Tool and saved-agent steps are the only capabilities that may touch external systems.");
        sb.AppendLine("- Put dynamic dependencies only under in using binding objects like {\"from\":\"$step.ref\",\"mode\":\"value|map\"}.");
        sb.AppendLine("- A binding object is a VALUE inside in. Example: in={\"url\":{\"from\":\"$search.results[0].url\",\"mode\":\"value\"}}. Never use binding fields like from or mode as input names.");
        sb.AppendLine("- Steps may reference only earlier steps.");
        sb.AppendLine("- mode='value' passes one resolved value into one call.");
        sb.AppendLine("- mode='map' runs the step once per array element.");
        sb.AppendLine("- Do not wrap literal tool inputs in helper objects like {\"value\":...}.");
        sb.AppendLine("- If input shape matters for an llm or saved-agent step, add binding field type.");
        sb.AppendLine("- LLM steps must provide systemPrompt, userPrompt, and out.");
        sb.AppendLine("- Saved-agent steps must provide userPrompt and out, and must not provide systemPrompt.");
        sb.AppendLine("- For llm and saved-agent steps, out must include format ('json' or 'string') and aggregate ('single', 'collect', or 'flatten').");
        sb.AppendLine("- When out.format='json', include out.schema. When out.format='string', schema may be omitted or set to {\"type\":\"string\"}.");
        sb.AppendLine("- Use out.aggregate='collect' for mapped single-item outputs, 'flatten' for mapped array outputs, and 'single' otherwise.");
        sb.AppendLine("- Prompts must be complete literal instructions. Do not embed $step refs or unresolved template placeholders like {name}, {{name}}, [[name]], <<name>>, or ${name} inside prompts.");
        sb.AppendLine("- Use the exact tool names and exact saved-agent ids from the catalog.");
        sb.AppendLine("- Add only the steps required to reach the user goal.");
        sb.AppendLine();
        sb.AppendLine("Reference syntax allowed inside binding objects:");
        sb.AppendLine("- {\"from\":\"$stepId\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[]\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[].nested\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[n]\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[n].nested\"}");
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendAgents(sb, _agentCatalog.ListAgents());
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendTools(sb, tools);
        sb.AppendLine();
        sb.AppendLine("Return only the JSON plan object. No markdown fences. No prose.");
        return sb.ToString();
    }

    private static string BuildPlanningUserPrompt(string userQuery) => userQuery;

    private static string BuildRepairPrompt(string originalUserPrompt, Exception error, string? previousDraftJson)
    {
        var errorMessage = error.Message;
        var issueBlock = error is PlanValidationException validationException
            ? $"\nValidation issue details:\n{PlanningJson.SerializeIndented(validationException.Issue)}"
            : string.Empty;
        var previousDraftBlock = string.IsNullOrWhiteSpace(previousDraftJson)
            ? string.Empty
            : $"\nPrevious invalid draft plan:\n{previousDraftJson}";

        return $"{originalUserPrompt}\n\nYour previous plan was invalid.\nValidation error: {errorMessage}{issueBlock}{previousDraftBlock}\n\nReturn a corrected JSON plan object only.\nEdit the previous draft minimally when possible.\nPreserve working step ids, bindings, and structure unless the validation issue requires a change.\nNon-negotiable requirements:\n- Top-level object must include goal and steps.\n- Use only listed tools and saved agents.\n- Keep the plan as short as possible.\n- Every step must include id, kind, name, and in.\n- kind must be exactly one of 'tool', 'llm', or 'agent'.\n- name must be the exact tool name, llm label, or saved-agent id for the selected kind.\n- Put dynamic dependencies under in using binding objects as VALUES. Example: in={{\"url\":{{\"from\":\"$search.results[0].url\",\"mode\":\"value\"}}}}. Never use from or mode as input names.\n- Use only refs to earlier steps.\n- LLM steps must provide systemPrompt, userPrompt, and out.\n- Saved-agent steps must provide userPrompt and out, and must not provide systemPrompt.\n- For llm and saved-agent steps, out must include format ('json' or 'string') and aggregate ('single', 'collect', or 'flatten').\n- When out.format='json', include out.schema.\n- Use out.aggregate='collect' for mapped single-item outputs, 'flatten' for mapped array outputs, and 'single' otherwise.\n- Prompts must be literal text. Do not embed $step refs or unresolved template placeholders like {{name}}, {{{{name}}}}, [[name]], <<name>>, or ${{name}}.\n- Do not return markdown fences or prose.\nDo not repeat the same mistake.";
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

    private static PlanDefinition ClonePlan(PlanDefinition plan, PlanModelProfile profile) =>
        PlanSanitizer.CloneSanitized(plan, profile);
}
