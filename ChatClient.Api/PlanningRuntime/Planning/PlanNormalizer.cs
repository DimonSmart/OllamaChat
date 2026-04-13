using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanNormalizer
{
    public static PlanDefinition Normalize(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        for (var index = 0; index < plan.Steps.Count; index++)
            plan.Steps[index] = NormalizeStep(plan.Steps[index]);

        AutoMarkResultStepIfMissing(plan);
        return plan;
    }

    /// <summary>
    /// If no step already has <see cref="PlanStep.IsResult"/> set to <c>true</c>,
    /// marks the last terminal step (the step with no downstream consumers) as the result.
    /// This ensures every normalised plan has an explicit result designation without
    /// requiring the initial LLM draft to explicitly set the field.
    /// </summary>
    private static void AutoMarkResultStepIfMissing(PlanDefinition plan)
    {
        if (plan.Steps.Count == 0)
            return;

        if (plan.Steps.Any(static s => s.IsResult))
            return;

        // Find the last step that has no downstream consumers.
        var terminalIds = PlanDependencyGraph.GetTerminalStepIds(plan.Steps);
        var lastTerminalId = terminalIds.Count > 0 ? terminalIds[^1] : plan.Steps[^1].Id;
        var targetStep = plan.Steps.LastOrDefault(s => string.Equals(s.Id, lastTerminalId, StringComparison.Ordinal))
            ?? plan.Steps[^1];
        targetStep.IsResult = true;
    }

    private static PlanStep NormalizeStep(PlanStep step)
    {
        var normalizedKind = PlanStepKinds.TryNormalize(step.Kind, out var kind)
            ? kind
            : step.Kind?.Trim() ?? string.Empty;

        return new PlanStep
        {
            Id = step.Id,
            Kind = normalizedKind,
            CapabilityId = NormalizeCapabilityId(step.CapabilityId),
            SystemPrompt = step.SystemPrompt,
            UserPrompt = step.UserPrompt,
            In = NormalizeInputs(step.In),
            Out = NormalizeOutputContract(normalizedKind, step.Out),
            IsResult = step.IsResult,
            Status = step.Status,
            Result = step.Result,
            Error = step.Error
        };
    }

    private static Dictionary<string, JsonNode?> NormalizeInputs(IReadOnlyDictionary<string, JsonNode?> inputs)
    {
        var normalized = new Dictionary<string, JsonNode?>(inputs.Count, StringComparer.Ordinal);
        foreach (var input in inputs)
            normalized[input.Key] = NormalizeInputValue(input.Value);

        return normalized;
    }

    private static JsonNode? NormalizeInputValue(JsonNode? value)
    {
        if (!PlanInputBindingSyntax.TryParseBinding(value, out var bindingExpression, out var error)
            || !string.IsNullOrWhiteSpace(error)
            || bindingExpression is null)
        {
            return value?.DeepClone();
        }

        return bindingExpression switch
        {
            PlanInputBindingSpec binding => CreateBindingNode(binding),
            PlanInputConcatBindingSpec concat => CreateConcatBindingNode(concat),
            _ => value?.DeepClone()
        };
    }

    private static JsonObject CreateBindingNode(PlanInputBindingSpec binding)
    {
        var node = new JsonObject
        {
            ["from"] = binding.From,
            ["mode"] = binding.Mode == PlanInputBindingMode.Map ? "map" : "value"
        };

        if (!string.IsNullOrWhiteSpace(binding.Type))
            node["type"] = binding.Type.Trim();

        return node;
    }

    private static JsonObject CreateConcatBindingNode(PlanInputConcatBindingSpec concat)
    {
        var node = new JsonObject
        {
            ["concat"] = new JsonArray(concat.Concat.Select(static binding => (JsonNode?)CreateBindingNode(binding)).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(concat.Type))
            node["type"] = concat.Type.Trim();

        return node;
    }

    private static string? NormalizeCapabilityId(string? capabilityId)
    {
        var trimmed = capabilityId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : trimmed;
    }

    private static PlanStepOutputContract? NormalizeOutputContract(
        string normalizedKind,
        PlanStepOutputContract? explicitContract)
    {
        if (string.Equals(normalizedKind, PlanStepKinds.Tool, StringComparison.Ordinal))
            return null;

        if (explicitContract is null)
            return null;

        var normalizedFormat = PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var format)
            ? format
            : explicitContract.Format?.Trim() ?? string.Empty;

        return new PlanStepOutputContract
        {
            Format = normalizedFormat,
            Schema = explicitContract.Schema?.Clone()
        };
    }
}
