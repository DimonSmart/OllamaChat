using ChatClient.Api.PlanningRuntime.Shared;

namespace ChatClient.Api.PlanningRuntime.Outline;

public sealed class OutlinePlanningRequest
{
    public string UserQuery { get; init; } = string.Empty;

    public string ResultExpectations { get; init; } = string.Empty;

    public IReadOnlyCollection<CompactCapabilitySummary> Capabilities { get; init; } = [];
}

public interface IOutlinePlanner
{
    Task<PlanningStageResult<OutlinePlan>> CreatePlanAsync(
        OutlinePlanningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OutlinePlanner(IPlanningLlmClient llmClient) : IOutlinePlanner
{
    public async Task<PlanningStageResult<OutlinePlan>> CreatePlanAsync(
        OutlinePlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var generation = await llmClient.GenerateJsonAsync<OutlinePlan>(
            "outline_planner",
            OutlinePromptBuilder.BuildSystemPrompt(),
            OutlinePromptBuilder.BuildUserPrompt(
                request.UserQuery,
                request.ResultExpectations,
                request.Capabilities),
            cancellationToken);

        ValidateContract(generation);
        return new PlanningStageResult<OutlinePlan>
        {
            Plan = Normalize(generation.Result),
            RawResponse = generation.RawResponse
        };
    }

    private static void ValidateContract(PlanningJsonGenerationResult<OutlinePlan> generation)
    {
        var issues = new List<string>();
        if (generation.RawJson is not System.Text.Json.Nodes.JsonObject rawObject)
        {
            ValidateMaterializedFallback(generation.Result, issues);
            ThrowIfContractFailed(generation, issues);
            return;
        }

        RequireNonBlankString(rawObject, "goal", issues, "goal");
        var hasBlockedReason = HasNonBlankString(rawObject, "blockedReason");
        if (!hasBlockedReason)
            RequireNonBlankString(rawObject, "resultNodeId", issues, "resultNodeId");

        if (rawObject["nodes"] is not System.Text.Json.Nodes.JsonArray nodes || nodes.Count == 0)
        {
            issues.Add("nodes must be a non-empty array");
            ThrowIfContractFailed(generation, issues);
            return;
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not System.Text.Json.Nodes.JsonObject node)
            {
                issues.Add($"nodes[{index}] must be an object");
                continue;
            }

            RequireNonBlankString(node, "id", issues, $"nodes[{index}].id");
            RequireNonBlankString(node, "kind", issues, $"nodes[{index}].kind");
            RequireNonBlankString(node, "purpose", issues, $"nodes[{index}].purpose");
        }

        ThrowIfContractFailed(generation, issues);
    }

    private static void ValidateMaterializedFallback(OutlinePlan plan, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
            issues.Add("goal must be a non-empty string");

        if (!plan.IsBlocked && string.IsNullOrWhiteSpace(plan.ResultNodeId))
            issues.Add("resultNodeId must be a non-empty string when blockedReason is absent");

        if (plan.Nodes.Count == 0)
        {
            issues.Add("nodes must be a non-empty array");
            return;
        }

        for (var index = 0; index < plan.Nodes.Count; index++)
        {
            var node = plan.Nodes[index];
            if (string.IsNullOrWhiteSpace(node.Id))
                issues.Add($"nodes[{index}].id must be a non-empty string");
            if (string.IsNullOrWhiteSpace(node.Kind))
                issues.Add($"nodes[{index}].kind must be a non-empty string");
            if (string.IsNullOrWhiteSpace(node.Purpose))
                issues.Add($"nodes[{index}].purpose must be a non-empty string");
        }
    }

    private static void ThrowIfContractFailed(PlanningJsonGenerationResult<OutlinePlan> generation, List<string> issues)
    {
        if (issues.Count == 0)
            return;

        throw new PlanningContractException(
            stage: "outline",
            contractIssues: issues,
            rawResponse: generation.RawResponse,
            materializedJson: PlanningNodeJson.SerializeIndented(generation.Result));
    }

    private static void RequireNonBlankString(
        System.Text.Json.Nodes.JsonObject jsonObject,
        string propertyName,
        List<string> issues,
        string path)
    {
        if (!HasNonBlankString(jsonObject, propertyName))
            issues.Add($"{path} must be a non-empty string");
    }

    private static bool HasNonBlankString(System.Text.Json.Nodes.JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName] is System.Text.Json.Nodes.JsonValue value
        && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text);

    private static OutlinePlan Normalize(OutlinePlan plan) =>
        new()
        {
            Goal = plan.Goal.Trim(),
            BlockedReason = TrimOrNull(plan.BlockedReason),
            ResultNodeId = TrimOrNull(plan.ResultNodeId),
            RequiredDeliverables = plan.RequiredDeliverables
                .Select(static value => value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList(),
            Nodes = plan.Nodes.Select(static node => new OutlineNode
            {
                Id = node.Id.Trim(),
                Kind = node.Kind.Trim().ToLowerInvariant(),
                Purpose = node.Purpose.Trim(),
                DependsOn = node.DependsOn
                    .Select(static value => value.Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToList(),
                Inputs = node.Inputs.Select(static input => new OutlineNodeInput
                {
                    Name = input.Name.Trim(),
                    SemanticType = input.SemanticType.Trim(),
                    FromNodeId = input.FromNodeId.Trim()
                }).ToList(),
                Outputs = node.Outputs.Select(static output => new OutlineNodeOutput
                {
                    Name = output.Name.Trim(),
                    SemanticType = output.SemanticType.Trim()
                }).ToList(),
                Constraints = node.Constraints
                    .Select(static value => value.Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToList(),
                Notes = node.Notes
                    .Select(static value => value.Trim())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToList()
            }).ToList()
        };

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
