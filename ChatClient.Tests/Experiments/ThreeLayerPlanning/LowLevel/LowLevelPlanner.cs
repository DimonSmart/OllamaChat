using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.LowLevel;

public sealed class LowLevelPlanningRequest
{
    public OutlinePlan OutlinePlan { get; init; } = new();

    public IReadOnlyCollection<CompactCapabilitySummary> Capabilities { get; init; } = [];
}

public sealed class LowLevelPlanner(IExperimentLlmClient llmClient)
{
    public async Task<PlannerStageResult<LowLevelPlan>> CreatePlanAsync(
        LowLevelPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var generation = await llmClient.GenerateJsonAsync<LowLevelPlan>(
            "low_level_planner",
            LowLevelPromptBuilder.BuildSystemPrompt(),
            LowLevelPromptBuilder.BuildUserPrompt(request.OutlinePlan, request.Capabilities),
            cancellationToken);

        ValidateContract(generation);
        return new PlannerStageResult<LowLevelPlan>
        {
            Plan = Normalize(generation.Result),
            RawResponse = generation.RawResponse
        };
    }

    private static void ValidateContract(ExperimentJsonGenerationResult<LowLevelPlan> generation)
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
            RequireNonBlankString(rawObject, "resultStepId", issues, "resultStepId");

        if (rawObject["steps"] is not System.Text.Json.Nodes.JsonArray steps || steps.Count == 0)
        {
            issues.Add("steps must be a non-empty array");
            ThrowIfContractFailed(generation, issues);
            return;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index] is not System.Text.Json.Nodes.JsonObject step)
            {
                issues.Add($"steps[{index}] must be an object");
                continue;
            }

            RequireNonBlankString(step, "id", issues, $"steps[{index}].id");
            RequireNonBlankString(step, "outlineNodeId", issues, $"steps[{index}].outlineNodeId");
            RequireNonBlankString(step, "kind", issues, $"steps[{index}].kind");
            RequireNonBlankString(step, "purpose", issues, $"steps[{index}].purpose");

            var kind = TryGetString(step, "kind");
            if (string.Equals(kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, LowLevelStepKinds.Agent, StringComparison.OrdinalIgnoreCase))
            {
                RequireNonBlankString(step, "capabilityId", issues, $"steps[{index}].capabilityId");
            }

            if ((string.Equals(kind, LowLevelStepKinds.Llm, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(kind, LowLevelStepKinds.Agent, StringComparison.OrdinalIgnoreCase))
                && (step["out"] is not System.Text.Json.Nodes.JsonObject outObject || !HasNonBlankString(outObject, "format")))
            {
                issues.Add($"steps[{index}].out.format must be a non-empty string");
            }
        }

        ThrowIfContractFailed(generation, issues);
    }

    private static void ValidateMaterializedFallback(LowLevelPlan plan, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
            issues.Add("goal must be a non-empty string");

        if (!plan.IsBlocked && string.IsNullOrWhiteSpace(plan.ResultStepId))
            issues.Add("resultStepId must be a non-empty string when blockedReason is absent");

        if (plan.Steps.Count == 0)
        {
            issues.Add("steps must be a non-empty array");
            return;
        }

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (string.IsNullOrWhiteSpace(step.Id))
                issues.Add($"steps[{index}].id must be a non-empty string");
            if (string.IsNullOrWhiteSpace(step.OutlineNodeId))
                issues.Add($"steps[{index}].outlineNodeId must be a non-empty string");
            if (string.IsNullOrWhiteSpace(step.Kind))
                issues.Add($"steps[{index}].kind must be a non-empty string");
            if (string.IsNullOrWhiteSpace(step.Purpose))
                issues.Add($"steps[{index}].purpose must be a non-empty string");
        }
    }

    private static void ThrowIfContractFailed(ExperimentJsonGenerationResult<LowLevelPlan> generation, List<string> issues)
    {
        if (issues.Count == 0)
            return;

        throw new PlannerContractException(
            stage: "low_level",
            contractIssues: issues,
            rawResponse: generation.RawResponse,
            materializedJson: ExperimentJson.SerializeIndented(generation.Result));
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
        TryGetString(jsonObject, propertyName) is { } text && !string.IsNullOrWhiteSpace(text);

    private static string? TryGetString(System.Text.Json.Nodes.JsonObject jsonObject, string propertyName) =>
        jsonObject[propertyName] is System.Text.Json.Nodes.JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;

    private static LowLevelPlan Normalize(LowLevelPlan plan) =>
        new()
        {
            Goal = plan.Goal.Trim(),
            BlockedReason = TrimOrNull(plan.BlockedReason),
            OutlineResultNodeId = TrimOrNull(plan.OutlineResultNodeId),
            ResultStepId = TrimOrNull(plan.ResultStepId),
            Steps = plan.Steps.Select(static step => new LowLevelStep
            {
                Id = step.Id.Trim(),
                OutlineNodeId = step.OutlineNodeId.Trim(),
                Kind = step.Kind.Trim().ToLowerInvariant(),
                CapabilityId = TrimOrNull(step.CapabilityId),
                Purpose = step.Purpose.Trim(),
                Fanout = string.IsNullOrWhiteSpace(step.Fanout)
                    ? LowLevelFanoutModes.Single
                    : step.Fanout.Trim().ToLowerInvariant(),
                Inputs = step.Inputs.Select(static input => new LowLevelStepInput
                {
                    Name = input.Name.Trim(),
                    Source = new LowLevelInputSource
                    {
                        Kind = input.Source.Kind.Trim().ToLowerInvariant(),
                        Value = ExperimentJson.CloneNode(input.Source.Value),
                        StepId = TrimOrNull(input.Source.StepId),
                        Port = TrimOrNull(input.Source.Port),
                        Mode = TrimOrNull(input.Source.Mode)?.ToLowerInvariant()
                    }
                }).ToList(),
                Outputs = step.Outputs.Select(static output => new LowLevelStepOutput
                {
                    Name = output.Name.Trim(),
                    SemanticType = output.SemanticType.Trim()
                }).ToList(),
                Out = step.Out is null
                    ? null
                    : new LowLevelStepOutputSettings
                    {
                        Format = step.Out.Format.Trim().ToLowerInvariant()
                    },
                IsResult = step.IsResult
            }).ToList()
        };

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
