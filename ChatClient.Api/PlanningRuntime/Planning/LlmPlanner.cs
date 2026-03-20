using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Generates a PlanDefinition from a natural-language user query by asking the LLM.
/// The planner lists available workflow building blocks (tool steps) and LLM reasoning
/// steps, making it clear that only tool steps can access external systems.
/// </summary>
public sealed class LlmPlanner(
    IChatClient chatClient,
    PlanningToolCatalog toolCatalog,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null,
    IInitialDraftRepairer? initialDraftRepairer = null) : IPlanner
{
    private const int MaxDraftGenerations = 2;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
    private readonly IInitialDraftRepairer? _initialDraftRepairer = initialDraftRepairer;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlanDefinition> CreatePlanAsync(string userQuery, CancellationToken cancellationToken = default)
    {
        _log.Log($"[plan] create:start toolCount={toolCatalog.ListTools().Count} query={Shorten(userQuery, 240)}");
        _observer.OnEvent(new PlanningAttemptStartedEvent(1, "plan", userQuery));
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
            try
            {
                var response = await agent.RunAsync<ResultEnvelope<PlanDefinition>>(planningPrompt, null, JsonOptions, null, cancellationToken);
                var envelope = response.Result
                    ?? throw new InvalidOperationException("Planner returned an empty response envelope.");
                var plan = envelope.GetRequiredDataOrThrow("Planner");
                previousDraftJson = PlanningJson.SerializeIndented(plan);
                if (!PlanValidator.TryValidate(plan, tools, out var validationIssue))
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
                                DraftPlan = ClonePlan(plan),
                                ValidationIssue = validationIssue!
                            }, cancellationToken);

                            if (!PlanValidator.TryValidate(repaired, tools, out var repairedValidationIssue))
                                throw new PlanValidationException(repairedValidationIssue!);

                            _log.Log($"[plan] create:success steps={repaired.Steps.Count} goal={Shorten(repaired.Goal, 240)}");
                            _log.Log($"[plan] create:summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(repaired))}");
                            _observer.OnEvent(new PlanCreatedEvent(attempt, "plan", ClonePlan(repaired)));
                            return repaired;
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
                _log.Log($"[plan] create:summary {PlanningJson.SerializeNodeCompact(PlanningLogFormatter.SummarizePlan(plan))}");
                _observer.OnEvent(new PlanCreatedEvent(attempt, "plan", ClonePlan(plan)));
                return plan;
            }
            catch (Exception ex) when (attempt < MaxDraftGenerations)
            {
                lastError = ex;
                _log.Log($"[plan] create:retry attempt={attempt} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Retry {attempt}: {Shorten(ex.Message, 240)}"));
                planningPrompt = BuildRepairPrompt(userPrompt, ex, previousDraftJson);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid plan after {MaxDraftGenerations} draft generations. Last error: {lastError?.Message}",
            lastError);
    }

    private static string BuildSystemPrompt(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a planning agent. Given a user request, produce an execution plan.");
        sb.AppendLine("Return a COMPLETE and VALID plan envelope on the first try.");
        sb.AppendLine("A plan is an ordered list of workflow steps. There are exactly two kinds of steps:");
        sb.AppendLine();
        sb.AppendLine("1. Tool steps: use field \"tool\": \"<name>\".");
        sb.AppendLine("   Tool steps are the ONLY way to access external data.");
        sb.AppendLine("2. LLM steps: use field \"llm\": \"<free label>\".");
        sb.AppendLine("   LLM steps have NO tool access and NO internet access.");
        sb.AppendLine("   They can only reason over outputs produced by earlier tool steps.");
        sb.AppendLine();
        sb.AppendLine("Required top-level JSON shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"ok\": true|false,");
        sb.AppendLine("  \"data\": <plan|null>,");
        sb.AppendLine("  \"error\": null|{");
        sb.AppendLine("    \"code\": \"string\",");
        sb.AppendLine("    \"message\": \"string\",");
        sb.AppendLine("    \"details\": { }|null");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("When planning succeeds, return ok=true, error=null, and put the complete plan into data.");
        sb.AppendLine("When planning fails, return ok=false, data=null, and put the failure reason into error.");
        sb.AppendLine();
        sb.AppendLine("The plan inside data must use this exact JSON shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"goal\": \"string\",");
        sb.AppendLine("  \"steps\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"string\",");
        sb.AppendLine("      \"tool\": \"tool-name\",");
        sb.AppendLine("      \"in\": { \"param\": \"literal or {\\\"from\\\":\\\"$ref\\\",\\\"mode\\\":\\\"value|map\\\",\\\"type\\\":\\\"string|object|array<object>|...\\\"}\" },");
        sb.AppendLine("      \"s\": \"todo\",");
        sb.AppendLine("      \"res\": null,");
        sb.AppendLine("      \"err\": null");
        sb.AppendLine("    },");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"string\",");
        sb.AppendLine("      \"llm\": \"step-label\",");
        sb.AppendLine("      \"systemPrompt\": \"string\",");
        sb.AppendLine("      \"userPrompt\": \"string\",");
        sb.AppendLine("      \"in\": { \"param\": \"literal or {\\\"from\\\":\\\"$ref\\\",\\\"mode\\\":\\\"value|map\\\",\\\"type\\\":\\\"string|object|array<object>|...\\\"}\" },");
        sb.AppendLine("      \"out\": {");
        sb.AppendLine("        \"format\": \"json|string\",");
        sb.AppendLine("        \"aggregate\": \"single|collect|flatten\",");
        sb.AppendLine("        \"schema\": { }");
        sb.AppendLine("      },");
        sb.AppendLine("      \"s\": \"todo\",");
        sb.AppendLine("      \"res\": null,");
        sb.AppendLine("      \"err\": null");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("General planning rules:");
        sb.AppendLine("- Prefer the SHORTEST plan that can succeed.");
        sb.AppendLine("- Search and download steps retrieve PAGES or DOCUMENTS, not selected entities. Do not assume 'N pages' means 'N products/packages/libraries'.");
        sb.AppendLine("- When the user asks to compare, rank, review, or recommend a small number of discovered entities, prefer this shape:");
        sb.AppendLine("  discovery search -> shortlist exact entities -> targeted retrieval for each chosen entity -> extract facts for that entity -> synthesize final answer.");
        sb.AppendLine("- Use shortlist/select steps whenever a discovery page, roundup article, search result, or catalog page may mention multiple entities.");
        sb.AppendLine("- If a page can mention multiple entities, do not feed it into a single-entity extractor unless that extractor also receives an explicit target entity name/id/model.");
        sb.AppendLine("- For final comparison or recommendation steps, instruct the LLM to explicitly mention the compared item names and the reason for the choice.");
        sb.AppendLine("- Preserve explicit deliverables from the user request, such as official docs links, NuGet/package pages, GitHub URLs, ranked lists, pros/cons, or citations.");
        sb.AppendLine("- If the user explicitly asks for official pages, package pages, repo links, docs links, or multiple evidence sources per entity, plan separate retrieval for those sources.");
        sb.AppendLine("- Unless the user explicitly asked for official pages, documentation pages, repo URLs, or extra evidence sources, do NOT require them in shortlist/select prompts.");
        sb.AppendLine("- If the current search results already appear to point at candidate entity pages that downstream download can inspect, shortlist those result URLs directly and carry them forward as evidence.");
        sb.AppendLine("- Avoid a second search unless the first downloaded pages clearly cannot contain the requested facts, but DO use targeted follow-up searches when precise entity-level evidence is required.");
        sb.AppendLine("- If the user asks for two or three items, discovery may over-fetch a little, but do not stop at arbitrary top-N pages. First identify the best entity candidates, then retrieve evidence for those entities.");
        sb.AppendLine("- Use input binding objects for every dynamic dependency: {\"from\":\"$step.ref\",\"mode\":\"value|map\"}.");
        sb.AppendLine("- mode='value' passes one resolved value into one call.");
        sb.AppendLine("- mode='map' means run the step once per array element.");
        sb.AppendLine("- For LLM steps, when input shape matters, add a compact declared type directly inside the binding object using field 'type'. Example: {\"from\":\"$search.results\",\"mode\":\"value\",\"type\":\"array<object>\"}.");
        sb.AppendLine("- The binding field 'type' describes the single-call resolved input. Example: mode='value' with $search.results usually means type='array<object>', while mode='map' with $search.results usually means type='object'.");
        sb.AppendLine("- Supported binding types are: string, number, integer, boolean, object, array, array<string>, array<number>, array<integer>, array<boolean>, array<object>.");
        sb.AppendLine("- If a tool returns an object containing an array field, bind from that field directly (for example: {\"from\":\"$search.results\",\"mode\":\"map\"}).");
        sb.AppendLine("- If a downstream tool needs a projected array field, express it explicitly in the ref (for example: {\"from\":\"$search.results[].url\",\"mode\":\"map\"}).");
        sb.AppendLine("- When chaining search-style results into download-style tools, use input key 'page' when you have full page-reference objects, or input key 'url' when you only have raw URLs.");
        sb.AppendLine("- A download-style tool's 'page' input is a page-reference object that must contain at least 'url'; title and other search metadata may be optional.");
        sb.AppendLine("- Download-style tools may return the page reference enriched with 'content'. Prefer consuming title/content from the download result, and keep search metadata when it adds value.");
        sb.AppendLine("- For download-style tools, pass exactly one of 'page' or 'url'. Do not send both.");
        sb.AppendLine("- Tool steps must use the exact tool name listed below, including any server prefix.");
        sb.AppendLine("- Do not invent, estimate, or fill in exact factual values when the user request requires precise facts.");
        sb.AppendLine("- If exact values are missing, add retrieval/narrowing steps or let the downstream step fail through the structured execution error contract instead of guessing.");
        sb.AppendLine("- For extraction steps, if the source does not contain the requested entity or the critical facts are absent, rely on the structured execution error contract instead of guessing.");
        sb.AppendLine("- Do not wrap literal tool arguments in helper objects like {\"value\":...}. Tool inputs must be plain literals or binding objects with exactly 'from' and optional 'mode'. LLM bindings may additionally include optional field 'type'.");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in tools)
        {
            sb.AppendLine($"- name: {tool.QualifiedName}");
            sb.AppendLine($"  description: {tool.Description}");
            sb.AppendLine($"  inputSchema: {PlanningJson.SerializeElementCompact(tool.InputSchema)}");
            sb.AppendLine($"  outputSchema: {PlanningJson.SerializeElementCompact(tool.OutputSchema)}");
        }

        sb.AppendLine();
        sb.AppendLine("Reference syntax allowed inside binding objects:");
        sb.AppendLine("- {\"from\":\"$stepId\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[]\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[].nested\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[n]\"}");
        sb.AppendLine("- {\"from\":\"$stepId.field[n].nested\"}");
        sb.AppendLine("- Add \"mode\":\"map\" when the resolved value is an array and the step must run once per item.");
        sb.AppendLine();
        sb.AppendLine("LLM step rules:");
        sb.AppendLine("- LLM steps MUST provide both systemPrompt and userPrompt.");
        sb.AppendLine("- Never omit userPrompt, even for shortlist/select/extract/compare steps. If needed, use a short literal sentence.");
        sb.AppendLine("- The executor appends an Input section with all resolved 'in' values.");
        sb.AppendLine("- Do NOT use template placeholders such as {var} or {{var}} in prompts.");
        sb.AppendLine("- Do NOT put step refs like $stepId or $stepId.field inside systemPrompt or userPrompt. All dynamic data must come through binding objects in 'in'.");
        sb.AppendLine("- out must be an object with format, aggregate, and optional schema.");
        sb.AppendLine("- Prefer out.format='json' unless the final answer is intentionally plain text.");
        sb.AppendLine("- If out.format='json', include out.schema that describes the expected JSON shape.");
        sb.AppendLine("- Every schema node must declare either type or enum.");
        sb.AppendLine("- If out.schema.type='object', every entry inside out.schema.properties must itself declare type or enum.");
        sb.AppendLine("- If out.schema.type='array', out.schema.items must declare type or enum.");
        sb.AppendLine("- If out.format='string', either omit out.schema or use out.schema={\"type\":\"string\"}.");
        sb.AppendLine("- If your extraction prompt says a field should be null when missing, reflect that in the schema with nullable=true or type=[\"<base>\",\"null\"].");
        sb.AppendLine("- If the step uses mode='map' and each call returns one item, use out.aggregate='collect'.");
        sb.AppendLine("- If the step uses mode='map' and each call returns an array of items, use out.aggregate='flatten'.");
        sb.AppendLine("- If the step does not use mode='map', use out.aggregate='single'.");
        sb.AppendLine("- If out.aggregate='collect', out.schema must describe one call result item, not the final collected array.");
        sb.AppendLine("- If out.aggregate='flatten', out.schema must describe the per-call array shape that will be flattened.");
        sb.AppendLine("- Use the exact field name \"userPrompt\". Do not use aliases like \"prompt\" or \"instruction\".");
        sb.AppendLine("- Put step parameters under \"in\". Do not place tool args as top-level step properties.");
        sb.AppendLine("- Every step must include id, in, s, res, and err.");
        sb.AppendLine();
        sb.AppendLine("Few-shot examples:");
        sb.AppendLine(BuildFewShotExamples());
        sb.AppendLine();
        sb.AppendLine("Return only the JSON envelope. No markdown fences. No prose outside the JSON.");
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

        return $"{originalUserPrompt}\n\nYour previous plan was invalid.\nValidation error: {errorMessage}{issueBlock}{previousDraftBlock}\n\nReturn a corrected ResultEnvelope<PlanDefinition> as JSON only.\nEdit the previous draft minimally when possible. Preserve correct step ids, bindings, and working structure instead of rewriting unrelated parts.\nNon-negotiable requirements:\n- Follow the exact ok/data/error envelope schema.\n- Put the full plan inside data when ok=true.\n- Each step must include id, in, s, res, and err.\n- A step must have exactly one of tool or llm.\n- Every llm step must include BOTH systemPrompt AND userPrompt, including shortlist/select/extract/compare steps.\n- Each llm step must include an out object with format/aggregate/schema.\n- Do not embed $step refs inside prompts.\n- Put dynamic inputs under in using binding objects like {{\"from\":\"$step.ref\",\"mode\":\"value|map\"}}.\n- If an llm input is shape-sensitive, add inline field \"type\" inside the binding object, for example {{\"from\":\"$search.results\",\"mode\":\"value\",\"type\":\"array<object>\"}}.\n- The inline binding field \"type\" describes one resolved call input. With {{\"mode\":\"value\"}} on $search.results this is often array<object>; with {{\"mode\":\"map\"}} on $search.results this is often object.\n- Supported inline binding types are string, number, integer, boolean, object, array, array<string>, array<number>, array<integer>, array<boolean>, array<object>.\n- Literal tool inputs must be plain JSON literals, never wrapper objects like {{\"value\":...}}.\n- Search/download steps return pages, not chosen entities. If the task compares or reviews a few entities, add shortlist/selection and targeted retrieval instead of assuming top pages equal the final entities.\n- If an extraction step would read a page that may mention multiple entities, either add an explicit target entity input or add a shortlist/select step first.\n- Preserve explicit user deliverables such as links, package pages, docs pages, repo URLs, ranking, and recommendation fields.\n- Unless the user explicitly asked for official pages, documentation pages, repo URLs, or extra evidence sources, do NOT require them in shortlist/select prompts.\n- If current search results already point to usable candidate pages, shortlist and download those URLs directly instead of adding another search just to find a more official-looking page.\n- Use collect for mapped single-item extraction, flatten for mapped multi-item extraction, and single otherwise.\n- Every schema node must declare type or enum.\n- Every object property schema must declare type or enum.\n- For string outputs, either omit schema or use {{\"type\":\"string\"}}.\n- Use the exact field names from the schema.\n- Use only refs to earlier steps.\nDo not repeat the same mistake.";
    }

    private static string BuildFewShotExamples() =>
        """
        Example A:
        User request: "Find two popular markdown parsers for .NET, extract license and GitHub repo URL, then recommend one."
        Good plan shape:
        1. search discovery pages
        2. shortlist exactly two parser packages using the best current result URL for each package
        3. retrieve package/repo evidence for each shortlisted parser
        4. extract structured facts for each targeted parser
        5. compare extracted facts in one final LLM step

        Example B:
        User request: "Compare two USB microphones by specs and summarize which is better for streaming."
        Good plan shape:
        1. search product discovery pages
        2. shortlist two specific microphone models using the best current result URL for each model
        3. retrieve one evidence page per shortlisted model
        4. extract model name, pickup pattern, sample rate, connectivity, and price for each explicit model
        5. synthesize a recommendation that names both products and states reasons

        Example C:
        User request: "Look up two train-route planning libraries and tell me which one better matches small hobby projects."
        Good plan shape:
        1. search discovery pages
        2. shortlist two specific libraries using the best current result URL for each library
        3. retrieve docs or repo pages for each shortlisted library
        4. extract maintenance and onboarding facts for each explicit library
        5. return a final recommendation

        Example output contracts:
        - one object: {"format":"json","aggregate":"single","schema":{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}}
        - mapped objects collected into an array: {"format":"json","aggregate":"collect","schema":{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}}
        - mapped arrays flattened into one array: {"format":"json","aggregate":"flatten","schema":{"type":"array","items":{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}}}
        - final plain text: {"format":"string","aggregate":"single","schema":{"type":"string"}}
        """;

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
        ?? throw new InvalidOperationException("Failed to clone plan.");
}

