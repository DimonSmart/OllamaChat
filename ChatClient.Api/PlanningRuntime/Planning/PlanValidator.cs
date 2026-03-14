using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static partial class PlanValidator
{
    public static void ValidateOrThrow(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
            throw new InvalidOperationException("Plan.goal is required.");

        if (plan.Steps.Count == 0)
            throw new InvalidOperationException("Plan.steps must contain at least one step.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var knownTools = tools?
            .ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);

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

            AppToolDescriptor? toolMetadata = null;
            if (hasTool && knownTools is not null)
            {
                if (!knownTools.TryGetValue(step.Tool!, out toolMetadata))
                    throw new InvalidOperationException($"Step '{step.Id}' references unknown tool '{step.Tool}'.");

                ValidateToolInputs(step, toolMetadata);
            }

            foreach (var input in step.In)
                ValidateInputOrThrow(step.Id, input.Key, input.Value, seenIds);

            ValidateOutputContractOrThrow(step, toolMetadata);

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
        }
    }

    private static void ValidateToolInputs(PlanStep step, AppToolDescriptor toolMetadata)
    {
        if (toolMetadata.InputSchema.ValueKind != JsonValueKind.Object ||
            !toolMetadata.InputSchema.TryGetProperty("properties", out var propertiesNode) ||
            propertiesNode.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Tool '{toolMetadata.QualifiedName}' has invalid input schema. It must define an object 'properties' map.");
        }

        var properties = propertiesNode.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var inputName in step.In.Keys)
        {
            if (!properties.Contains(inputName))
            {
                throw new InvalidOperationException(
                    $"Tool step '{step.Id}' passes unknown input '{inputName}' to tool '{toolMetadata.QualifiedName}'.");
            }
        }
    }

    private static void ValidateInputOrThrow(
        string stepId,
        string inputName,
        JsonNode? value,
        HashSet<string> knownStepIds)
    {
        if (PlanInputBindingSyntax.TryGetLegacyStringReference(value, out var legacyReference))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' input '{inputName}' uses legacy string ref syntax '{legacyReference}'. Use a binding object like {{\"from\":\"{legacyReference}\",\"mode\":\"value\"}}.");
        }

        if (!PlanInputBindingSyntax.TryParseBinding(value, out var binding, out var bindingError))
            return;

        if (!string.IsNullOrWhiteSpace(bindingError))
            throw new InvalidOperationException($"Step '{stepId}' has invalid binding in input '{inputName}': {bindingError}");

        if (!PlanInputBindingSyntax.TryParseReference(binding!.From, out var reference, out var refError))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' has invalid ref syntax in input '{inputName}': '{binding.From}'. {refError}");
        }

        if (!knownStepIds.Contains(reference!.StepId))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' references '{binding.From}' before step '{reference.StepId}' is available.");
        }
    }

    private static void ValidateOutputContractOrThrow(PlanStep step, AppToolDescriptor? toolMetadata)
    {
        var issues = PlanStepOutputContractResolver.ValidateContractDefinition(step, toolMetadata);
        if (issues.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Step '{step.Id}' has invalid out contract: {string.Join(" ", issues)}");
    }

    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_\-.\[\]]*")]
    private static partial Regex PotentialEmbeddedRefPattern();

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

    private static bool ContainsPromptRef(string prompt)
    {
        foreach (Match match in PotentialEmbeddedRefPattern().Matches(prompt))
        {
            if (PlanInputBindingSyntax.TryParseReference(match.Value, out _, out _))
                return true;
        }

        return false;
    }

    private static bool ContainsTemplatePlaceholder(string prompt) =>
        DoubleBracePlaceholderPattern().IsMatch(prompt)
        || SingleBracePlaceholderPattern().IsMatch(prompt)
        || DoubleSquarePlaceholderPattern().IsMatch(prompt)
        || DoubleAnglePlaceholderPattern().IsMatch(prompt)
        || DollarBracePlaceholderPattern().IsMatch(prompt);
}
