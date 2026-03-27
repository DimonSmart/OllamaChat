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
                PlanNormalizer.Normalize(plan, tools);

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
                            PlanNormalizer.Normalize(repaired, tools);
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
        sb.AppendLine("Build the shortest correct executable plan that can actually satisfy the user request with the listed capabilities.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Return one JSON plan object with top-level goal and steps.");
        sb.AppendLine("- Use only the listed tools and saved agents. Never invent capabilities.");
        sb.AppendLine("- Think in user-visible deliverables first. Identify what the final answer must contain or what external action must happen.");
        sb.AppendLine("- Map every required external fact or external action to one listed external capability before adding execution steps.");
        sb.AppendLine("- The listed capabilities may support any domain or action. Infer the plan shape from each tool's actual description, schema, and compatibility metadata; do not assume a fixed workflow like search, download, compare, or summarize unless the catalog implies it.");
        sb.AppendLine("- LLM steps may transform, normalize, deduplicate, compare, rank, summarize, or validate only the evidence already present in their inputs. They must not introduce new facts.");
        sb.AppendLine("- Separate evidence acquisition from normalization/verification and final synthesis when evidence may be ambiguous, duplicated, incomplete, or candidate-based.");
        sb.AppendLine("- If a downstream tool must act on discovered records, preserve those exact records or a directly compatible projection instead of collapsing them to names or summaries too early.");
        sb.AppendLine("- If tool metadata says one tool's records are directly compatible with another tool input, keep those full compatible records through shortlist or ranking steps unless you truly need a narrower projection.");
        sb.AppendLine("- Example: if one tool returns records directly compatible with a later tool input, shortlist those full records and bind the later tool from that shortlist with mode='map'; do not reduce them to lossy summaries first.");
        sb.AppendLine("- Do not treat lightweight metadata such as titles, snippets, rankings, ids, or shortlist rationales as if they were the underlying source content.");
        sb.AppendLine("- If the user needs precise facts, quotes, specs, prices, dates, or side-by-side comparison fields and the current inputs only contain lightweight records, add a step that acquires richer source content when a listed capability can do so.");
        sb.AppendLine("- When the user asks for at least N distinct items, plan enough discovery or acquisition breadth first by using the relevant tool inputs and metadata. If a tool says a limit is only a maximum or that one call may return fewer than requested, reflect that in the plan by increasing breadth or adding another call. Do not make an intermediate llm step guarantee N items unless its inputs already guarantee that count.");
        sb.AppendLine("- If the available evidence may still yield fewer than N items, let the relevant llm step return the maximum supported set plus an insufficiency field or plan more discovery; do not silently force N unsupported items.");
        sb.AppendLine("- If the listed capabilities truly cannot obtain or verify a required deliverable, return the shortest blocked plan instead of pretending the task is executable. Do not use a blocked plan when an available capability could still fetch or inspect the missing source evidence.");
        sb.AppendLine("- A blocked plan should usually end with one llm step whose prompt explains the capability gap and instructs the step to return a blocked error with code 'insufficient_capabilities', details.status='blocked', details.needsReplan=false, and details.type='insufficient_capability'.");
        sb.AppendLine("- Every step must include id, kind, and in.");
        sb.AppendLine("- kind must be exactly one of 'tool', 'llm', or 'agent'.");
        sb.AppendLine("- Tool and saved-agent steps must include capabilityId.");
        sb.AppendLine("- For tool or saved-agent steps, capabilityId must be the exact tool id or saved-agent id from the catalog.");
        sb.AppendLine("- For generic llm steps, capabilityId is optional. Omit it unless a short literal label helps readability.");
        sb.AppendLine("- Tool and saved-agent steps are the only capabilities that may touch external systems.");
        sb.AppendLine("- Put dynamic dependencies only under in using binding objects like {\"from\":\"$step.ref\",\"mode\":\"value|map\"} or concat bindings like {\"concat\":[{\"from\":\"$s1.items\",\"mode\":\"value\"},{\"from\":\"$s2.items\",\"mode\":\"value\"}],\"type\":\"array<object>\"}.");
        sb.AppendLine("- A binding object is a VALUE inside in. Example: in={\"item\":{\"from\":\"$step1.items[0]\",\"mode\":\"value\"}}. Never use binding fields like from, mode, or concat as input names.");
        sb.AppendLine("- Steps may reference only earlier steps.");
        sb.AppendLine("- mode='value' passes one resolved value into one call.");
        sb.AppendLine("- mode='map' runs the step once per array element.");
        sb.AppendLine("- binding.mode supports ONLY 'value' or 'map'. Never use 'flatten' as a binding mode.");
        sb.AppendLine("- Invalid example: {\"from\":\"$s1.results[]\",\"mode\":\"flatten\"}.");
        sb.AppendLine("- Use concat only when you need to merge several array sources into one array input for a downstream step. Each concat item must resolve to an array and must use mode='value'.");
        sb.AppendLine("- Every non-final step must feed at least one downstream consumer. The only terminal step must be the last step.");
        sb.AppendLine("- Do not wrap literal tool inputs in helper objects like {\"value\":...}.");
        sb.AppendLine("- binding field type is optional. Add it only when a repair specifically needs an inline input hint.");
        sb.AppendLine("- Tool steps must not declare out. The runtime derives tool output contracts from the tool catalog.");
        sb.AppendLine("- LLM steps must provide systemPrompt, userPrompt, and out.");
        sb.AppendLine("- Saved-agent steps must provide userPrompt and out, and must not provide systemPrompt.");
        sb.AppendLine("- For llm and saved-agent steps, out must include format ('json' or 'string').");
        sb.AppendLine("- out.schema is optional. Use it only when you need to strengthen the output contract for an llm or saved-agent step.");
        sb.AppendLine("- Prompts must be complete literal instructions. Do not embed $step refs or unresolved template placeholders like {name}, {{name}}, [[name]], <<name>>, or ${name} inside prompts.");
        sb.AppendLine("- Use the exact tool ids and exact saved-agent ids from the catalog.");
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
        var sb = new StringBuilder();
        sb.Append(originalUserPrompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Your previous plan was invalid.");
        sb.AppendLine();
        sb.Append($"Validation error: {errorMessage}{issueBlock}{previousDraftBlock}");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Return a corrected JSON plan object only.");
        sb.AppendLine("Edit the previous draft minimally when possible.");
        sb.AppendLine("Preserve working step ids, bindings, and structure unless the validation issue requires a change.");
        sb.AppendLine("Non-negotiable requirements:");
        sb.AppendLine("- Top-level object must include goal and steps.");
        sb.AppendLine("- Use only listed tools and saved agents.");
        sb.AppendLine("- Keep the plan as short as possible.");
        sb.AppendLine("- Every step must include id, kind, and in.");
        sb.AppendLine("- kind must be exactly one of 'tool', 'llm', or 'agent'.");
        sb.AppendLine("- Tool and saved-agent steps must include capabilityId.");
        sb.AppendLine("- For tool or saved-agent steps, capabilityId must be the exact tool id or saved-agent id from the catalog.");
        sb.AppendLine("- For generic llm steps, capabilityId is optional.");
        sb.AppendLine("- Map each required external fact or external action to one listed capability before adding execution steps.");
        sb.AppendLine("- Derive the plan shape from the listed tool descriptions, schemas, and compatibility metadata. Do not assume a fixed workflow unless the catalog implies it.");
        sb.AppendLine("- LLM steps may transform or validate only the evidence already present in their inputs.");
        sb.AppendLine("- Do not treat titles, snippets, rankings, ids, or shortlist rationales as full source content when the user needs precise facts.");
        sb.AppendLine("- If the listed capabilities cannot obtain or verify a required deliverable, return the shortest blocked plan instead of pretending the task is executable.");
        sb.AppendLine("- A blocked plan should usually end with one llm step that returns code='insufficient_capabilities', details.status='blocked', details.needsReplan=false, and details.type='insufficient_capability'.");
        sb.AppendLine("- Put dynamic dependencies under in using binding objects as VALUES. Example: in={\"item\":{\"from\":\"$step1.items[0]\",\"mode\":\"value\"}}.");
        sb.AppendLine("- To merge several array sources into one array input, use concat. Example: {\"concat\":[{\"from\":\"$s1.items\",\"mode\":\"value\"},{\"from\":\"$s2.items\",\"mode\":\"value\"}],\"type\":\"array<object>\"}.");
        sb.AppendLine("- Never use from, mode, or concat as input names.");
        sb.AppendLine("- binding.mode supports only 'value' or 'map'. Never use 'flatten' as a binding mode.");
        sb.AppendLine("- Use only refs to earlier steps.");
        sb.AppendLine("- Every non-final step must feed at least one downstream consumer. The only terminal step must be the last step.");
        sb.AppendLine("- Tool steps must not declare out. The runtime derives tool output contracts from the tool catalog.");
        sb.AppendLine("- LLM steps must provide systemPrompt, userPrompt, and out.");
        sb.AppendLine("- Saved-agent steps must provide userPrompt and out, and must not provide systemPrompt.");
        sb.AppendLine("- For llm and saved-agent steps, out must include format ('json' or 'string').");
        sb.AppendLine("- out.schema is optional. Add it only when the validation issue requires a stronger explicit contract.");
        sb.AppendLine("- Prompts must be literal text. Do not embed $step refs or unresolved template placeholders like {name}, {{name}}, [[name]], <<name>>, or ${name}.");
        sb.AppendLine("- Do not return markdown fences or prose.");
        sb.Append("Do not repeat the same mistake.");
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

    private static PlanDefinition ClonePlan(PlanDefinition plan, PlanModelProfile profile) =>
        PlanSanitizer.CloneSanitized(plan, profile);
}
