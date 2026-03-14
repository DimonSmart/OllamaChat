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
    IPlanRunObserver? planRunObserver = null) : IPlanner
{
    private const int MaxPlanningAttempts = 5;
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;
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

        for (var attempt = 1; attempt <= MaxPlanningAttempts; attempt++)
        {
            try
            {
                var response = await agent.RunAsync<ResultEnvelope<PlanDefinition>>(planningPrompt, null, JsonOptions, null, cancellationToken);
                var envelope = response.Result
                    ?? throw new InvalidOperationException("Planner returned an empty response envelope.");
                var plan = envelope.GetRequiredDataOrThrow("Planner");
                PlanValidator.ValidateOrThrow(plan, tools);

                _log.Log($"[plan] create:success steps={plan.Steps.Count} goal={Shorten(plan.Goal, 240)}");
                _log.Log($"[plan] create:json {PlanningJson.SerializeIndented(plan)}");
                _observer.OnEvent(new PlanCreatedEvent(attempt, "plan", ClonePlan(plan)));
                return plan;
            }
            catch (Exception ex) when (attempt < MaxPlanningAttempts)
            {
                lastError = ex;
                _log.Log($"[plan] create:retry attempt={attempt} error={Shorten(ex.Message, 240)}");
                _observer.OnEvent(new DiagnosticPlanRunEvent("planner", $"Retry {attempt}: {Shorten(ex.Message, 240)}"));
                planningPrompt = BuildRepairPrompt(userPrompt, ex.Message);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid plan after {MaxPlanningAttempts} attempts. Last error: {lastError?.Message}",
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
        sb.AppendLine("      \"in\": { \"param\": \"literal or $ref\" },");
        sb.AppendLine("      \"s\": \"todo\",");
        sb.AppendLine("      \"res\": null,");
        sb.AppendLine("      \"err\": null");
        sb.AppendLine("    },");
        sb.AppendLine("    {");
        sb.AppendLine("      \"id\": \"string\",");
        sb.AppendLine("      \"llm\": \"step-label\",");
        sb.AppendLine("      \"systemPrompt\": \"string\",");
        sb.AppendLine("      \"userPrompt\": \"string\",");
        sb.AppendLine("      \"in\": { \"param\": \"literal or $ref\" },");
        sb.AppendLine("      \"out\": \"json|string\",");
        sb.AppendLine("      \"each\": true|false,");
        sb.AppendLine("      \"s\": \"todo\",");
        sb.AppendLine("      \"res\": null,");
        sb.AppendLine("      \"err\": null");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("General planning rules:");
        sb.AppendLine("- Prefer the SHORTEST plan that can succeed.");
        sb.AppendLine("- When the user asks to compare, rank, summarize, or compute over a small number of discovered items, prefer this shape:");
        sb.AppendLine("  search candidate pages -> download candidate pages -> extract structured facts from each page -> synthesize final answer.");
        sb.AppendLine("- For final comparison or recommendation steps, instruct the LLM to explicitly mention the compared item names and the reason for the choice.");
        sb.AppendLine("- Avoid a second search unless the first downloaded pages clearly cannot contain the requested facts.");
        sb.AppendLine("- If the user asks for two items, prefer to search for two candidate pages or a very small over-fetch, not many pages.");
        sb.AppendLine("- If a tool returns array data and the next tool expects a scalar parameter, pass the full array ref and let the executor fan out.");
        sb.AppendLine("- If a tool returns an object containing an array field, reference that field directly (for example: $search.results).");
        sb.AppendLine("- When chaining search-style results into download-style tools, prefer passing the whole array field into input key 'page' so metadata is preserved and download can add content.");
        sb.AppendLine("- If a downstream tool input is named 'url' and receives an array of objects that each contain 'url', the executor can auto-project those URLs.");
        sb.AppendLine("- Download-style tools may return the original page object enriched with 'content'. Prefer consuming title/content from the download result, and keep search metadata when it adds value.");
        sb.AppendLine("- For download-style tools, pass exactly one of 'page' or 'url'. Do not send both.");
        sb.AppendLine("- Tool steps must use the exact tool name listed below, including any server prefix.");
        sb.AppendLine("- Do not invent, estimate, or fill in exact factual values when the user request requires precise facts.");
        sb.AppendLine("- If exact values are missing, add retrieval/narrowing steps or let the downstream step fail through the structured execution error contract instead of guessing.");
        sb.AppendLine("- For extraction steps, if the source does not contain the requested entity or the critical facts are absent, rely on the structured execution error contract instead of guessing.");
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
        sb.AppendLine("Fan-out rules:");
        sb.AppendLine("- Tool steps: the executor automatically fans out when a scalar input receives an array.");
        sb.AppendLine("- LLM steps: set \"each\": true to call the LLM once per array element. Omit it or set false to pass the whole array in one call.");
        sb.AppendLine();
        sb.AppendLine("Reference syntax allowed in step inputs:");
        sb.AppendLine("- $stepId");
        sb.AppendLine("- $stepId.field");
        sb.AppendLine("- $stepId[]");
        sb.AppendLine("- $stepId[].field");
        sb.AppendLine("- $stepId[n]");
        sb.AppendLine("- $stepId[n].field");
        sb.AppendLine("Use $stepId.field only when the referenced step output is an object.");
        sb.AppendLine("Use $stepId[].field when the referenced step output is an array of objects.");
        sb.AppendLine("If a tool returns an object containing an array field, pass that field directly (for example: $search.results).");
        sb.AppendLine();
        sb.AppendLine("LLM step rules:");
        sb.AppendLine("- LLM steps MUST provide both systemPrompt and userPrompt.");
        sb.AppendLine("- The executor appends an Input section with all resolved 'in' values.");
        sb.AppendLine("- Do NOT use template placeholders such as {var} or {{var}} in prompts.");
        sb.AppendLine("- Do NOT put step refs like $stepId or $stepId.field inside systemPrompt or userPrompt. All dynamic data must come through 'in'.");
        sb.AppendLine("- out is \"json\" or \"string\". Prefer \"json\" unless the final answer is intentionally plain text.");
        sb.AppendLine("- When out is \"json\", the systemPrompt MUST explicitly require valid JSON only.");
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

    private static string BuildRepairPrompt(string originalUserPrompt, string errorMessage) =>
        $"{originalUserPrompt}\n\nYour previous plan was invalid.\nValidation error: {errorMessage}\n\nReturn a corrected ResultEnvelope<PlanDefinition> as JSON only.\nNon-negotiable requirements:\n- Follow the exact ok/data/error envelope schema.\n- Put the full plan inside data when ok=true.\n- Each step must include id, in, s, res, and err.\n- A step must have exactly one of tool or llm.\n- Each llm step must include systemPrompt, userPrompt, and out.\n- Do not embed $step refs inside prompts.\n- Use the exact field names from the schema.\n- Put all step inputs under in.\n- Use only refs to earlier steps.\nDo not repeat the same mistake.";

    private static string BuildFewShotExamples() =>
        """
        Example A:
        User request: "Find two popular markdown parsers for .NET, extract license and GitHub repo URL, then recommend one."
        Good plan shape:
        1. search candidate pages
        2. download candidate pages
        3. extract structured facts from each page with each=true
        4. compare extracted facts in one final LLM step

        Example B:
        User request: "Compare two USB microphones by specs and summarize which is better for streaming."
        Good plan shape:
        1. search product review pages
        2. download pages
        3. extract model name, pickup pattern, sample rate, connectivity, and price from each page
        4. synthesize a recommendation that names both products and states reasons

        Example C:
        User request: "Look up two train-route planning libraries and tell me which one better matches small hobby projects."
        Good plan shape:
        1. search docs or repo pages
        2. download those pages
        3. extract maintenance and onboarding facts from each page
        4. return a final recommendation
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

