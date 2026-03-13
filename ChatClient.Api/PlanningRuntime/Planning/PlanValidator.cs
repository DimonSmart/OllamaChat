using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Tools;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static partial class PlanValidator
{
    public static void ValidateOrThrow(
        PlanDefinition plan,
        IReadOnlyCollection<ToolPlannerMetadata>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
            throw new InvalidOperationException("Plan.goal is required.");

        if (plan.Steps.Count == 0)
            throw new InvalidOperationException("Plan.steps must contain at least one step.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var knownTools = tools?
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                throw new InvalidOperationException("Each step must have an id.");

            if (!seenIds.Add(step.Id))
                throw new InvalidOperationException($"Duplicate step id '{step.Id}'.");

            var hasTool = !string.IsNullOrWhiteSpace(step.Tool);
            var hasLlm = !string.IsNullOrWhiteSpace(step.Llm);
            if (hasTool == hasLlm)
                throw new InvalidOperationException($"Step '{step.Id}' must have exactly one of 'tool' or 'llm'.");

            if (!IsValidStatus(step.Status))
                throw new InvalidOperationException($"Step '{step.Id}' has invalid status '{step.Status}'.");

            if (step.In.Count == 0)
                throw new InvalidOperationException($"Step '{step.Id}' must declare its inputs in 'in'.");

            if (hasTool && knownTools is not null)
            {
                if (!knownTools.TryGetValue(step.Tool!, out var toolMetadata))
                    throw new InvalidOperationException($"Step '{step.Id}' references unknown tool '{step.Tool}'.");

                ValidateToolInputs(step, toolMetadata);
            }

            if (hasLlm)
            {
                if (string.IsNullOrWhiteSpace(step.SystemPrompt))
                    throw new InvalidOperationException($"LLM step '{step.Id}' must provide systemPrompt.");
                if (string.IsNullOrWhiteSpace(step.UserPrompt))
                    throw new InvalidOperationException($"LLM step '{step.Id}' must provide userPrompt.");
                if (ContainsPromptRef(step.SystemPrompt!))
                    throw new InvalidOperationException($"LLM step '{step.Id}' must not embed step refs inside systemPrompt.");
                if (ContainsPromptRef(step.UserPrompt!))
                    throw new InvalidOperationException($"LLM step '{step.Id}' must not embed step refs inside userPrompt.");
                if (ContainsTemplatePlaceholder(step.SystemPrompt!) || ContainsTemplatePlaceholder(step.UserPrompt!))
                {
                    throw new InvalidOperationException(
                        $"LLM step '{step.Id}' must not contain unresolved template placeholders like '{{name}}', '{{{{name}}}}', '[[name]]', '<<name>>', or '${{name}}' in prompts.");
                }
            }

            foreach (var input in step.In)
            {
                ValidateRefOrThrow(step.Id, input.Key, input.Value, seenIds);
            }
        }
    }

    private static void ValidateToolInputs(PlanStep step, ToolPlannerMetadata toolMetadata)
    {
        if (!toolMetadata.InputSchema.TryGetPropertyValue("properties", out var propertiesNode) || propertiesNode is not JsonObject properties)
        {
            throw new InvalidOperationException(
                $"Tool '{toolMetadata.Name}' has invalid planner metadata. Input schema must define an object 'properties' map.");
        }

        foreach (var inputName in step.In.Keys)
        {
            if (!properties.ContainsKey(inputName))
            {
                throw new InvalidOperationException(
                    $"Tool step '{step.Id}' passes unknown input '{inputName}' to tool '{toolMetadata.Name}'.");
            }
        }
    }

    private static void ValidateRefOrThrow(
        string stepId,
        string inputName,
        JsonNode? value,
        HashSet<string> knownStepIds)
    {
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || !text.StartsWith('$'))
            return;

        if (!RefPattern().IsMatch(text))
            throw new InvalidOperationException($"Step '{stepId}' has invalid ref syntax in input '{inputName}': '{text}'.");

        var referencedStepId = ExtractBaseStepId(text[1..]);
        if (!knownStepIds.Contains(referencedStepId))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' references '{text}' before step '{referencedStepId}' is available.");
        }
    }

    private static string ExtractBaseStepId(string expr)
    {
        var bracketPos = expr.IndexOf('[');
        var dotPos = expr.IndexOf('.');
        if (bracketPos >= 0 && dotPos >= 0)
            return expr[..Math.Min(bracketPos, dotPos)];
        if (bracketPos >= 0)
            return expr[..bracketPos];
        if (dotPos >= 0)
            return expr[..dotPos];
        return expr;
    }

    [GeneratedRegex(@"^\$[A-Za-z_][A-Za-z0-9_-]*(?:\[\]|\[\d+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*)?$")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_-]*(?:\[\]|\[\d+\])?(?:\.[A-Za-z_][A-Za-z0-9_]*)?")]
    private static partial Regex EmbeddedRefPattern();

    [GeneratedRegex(@"\{\{[^{}\r\n]+\}\}")]
    private static partial Regex DoubleBracePlaceholderPattern();

    [GeneratedRegex(@"(?<!\{)\{[A-Za-z_][A-Za-z0-9_.:-]*\}(?!\})")]
    private static partial Regex SingleBracePlaceholderPattern();

    [GeneratedRegex(@"\[\[[^\[\]\r\n]+\]\]")]
    private static partial Regex DoubleSquarePlaceholderPattern();

    [GeneratedRegex(@"<<[^<>\r\n]+>>")]
    private static partial Regex DoubleAnglePlaceholderPattern();

    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_.:-]*\}")]
    private static partial Regex DollarBracePlaceholderPattern();

    private static bool IsValidStatus(string? status) =>
        string.Equals(status, PlanStepStatuses.Todo, StringComparison.Ordinal)
        || string.Equals(status, PlanStepStatuses.Done, StringComparison.Ordinal)
        || string.Equals(status, PlanStepStatuses.Fail, StringComparison.Ordinal)
        || string.Equals(status, PlanStepStatuses.Skip, StringComparison.Ordinal);

    private static bool ContainsPromptRef(string prompt) =>
        EmbeddedRefPattern().IsMatch(prompt);

    private static bool ContainsTemplatePlaceholder(string prompt) =>
        DoubleBracePlaceholderPattern().IsMatch(prompt)
        || SingleBracePlaceholderPattern().IsMatch(prompt)
        || DoubleSquarePlaceholderPattern().IsMatch(prompt)
        || DoubleAnglePlaceholderPattern().IsMatch(prompt)
        || DollarBracePlaceholderPattern().IsMatch(prompt);
}

