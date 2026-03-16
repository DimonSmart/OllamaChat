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
        var validatedSteps = new Dictionary<string, PlanStep>(StringComparer.Ordinal);
        var resolvedOutputContracts = new Dictionary<string, ResolvedPlanStepOutputContract>(StringComparer.Ordinal);
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
            Dictionary<string, JsonElement>? toolInputProperties = null;
            if (hasTool && knownTools is not null)
            {
                if (!knownTools.TryGetValue(step.Tool!, out toolMetadata))
                    throw new InvalidOperationException($"Step '{step.Id}' references unknown tool '{step.Tool}'.");

                toolInputProperties = ValidateToolInputs(step, toolMetadata);
            }

            foreach (var input in step.In)
            {
                JsonElement? targetInputSchema = null;
                if (toolInputProperties is not null && toolInputProperties.TryGetValue(input.Key, out var propertySchema))
                    targetInputSchema = propertySchema;
                ValidateInputOrThrow(
                    step.Id,
                    input.Key,
                    input.Value,
                    seenIds,
                    validatedSteps,
                    resolvedOutputContracts,
                    targetInputSchema);
            }

            ValidateOutputContractOrThrow(step, toolMetadata);
            resolvedOutputContracts[step.Id] = PlanStepOutputContractResolver.Resolve(
                step,
                toolMetadata,
                PlanStepOutputContractResolver.HasMappedInputs(step));
            validatedSteps[step.Id] = step;

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

    private static Dictionary<string, JsonElement> ValidateToolInputs(PlanStep step, AppToolDescriptor toolMetadata)
    {
        if (toolMetadata.InputSchema.ValueKind != JsonValueKind.Object
            || !toolMetadata.InputSchema.TryGetProperty("properties", out var propertiesNode)
            || propertiesNode.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Tool '{toolMetadata.QualifiedName}' has invalid input schema. It must define an object 'properties' map.");
        }

        var properties = propertiesNode.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);

        if (toolMetadata.InputSchema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredProperty in requiredElement.EnumerateArray())
            {
                var propertyName = requiredProperty.GetString();
                if (string.IsNullOrWhiteSpace(propertyName))
                    continue;

                if (!step.In.ContainsKey(propertyName))
                {
                    throw new InvalidOperationException(
                        $"Tool step '{step.Id}' is missing required input '{propertyName}' for tool '{toolMetadata.QualifiedName}'.");
                }
            }
        }

        foreach (var input in step.In)
        {
            if (!properties.TryGetValue(input.Key, out var propertySchema))
            {
                throw new InvalidOperationException(
                    $"Tool step '{step.Id}' passes unknown input '{input.Key}' to tool '{toolMetadata.QualifiedName}'.");
            }

            if (PlanInputBindingSyntax.TryGetLegacyStringReference(input.Value, out _))
                continue;

            if (PlanInputBindingSyntax.TryParseBinding(input.Value, out _, out _))
                continue;

            var issues = ToolInputSchemaValidator.ValidateLiteralInput(input.Value, propertySchema);
            if (issues.Count == 0)
                continue;

            throw new InvalidOperationException(
                $"Tool step '{step.Id}' input '{input.Key}' does not match tool schema: {string.Join(" ", issues.Select(issue => issue.Message))}");
        }

        var wholeInputSchemaIssues = ToolInputSchemaValidator.ValidateLiteralInput(
            JsonSerializer.SerializeToNode(step.In),
            toolMetadata.InputSchema);
        if (wholeInputSchemaIssues.Count > 0)
        {
            var missingRequired = wholeInputSchemaIssues
                .Where(issue => string.Equals(issue.Code, "input_contract_missing_required", StringComparison.Ordinal))
                .Select(issue => issue.Message)
                .ToList();
            if (missingRequired.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Tool step '{step.Id}' does not match tool schema: {string.Join(" ", missingRequired)}");
            }
        }

        return properties;
    }

    private static void ValidateInputOrThrow(
        string stepId,
        string inputName,
        JsonNode? value,
        HashSet<string> knownStepIds,
        IReadOnlyDictionary<string, PlanStep> validatedSteps,
        IReadOnlyDictionary<string, ResolvedPlanStepOutputContract> resolvedOutputContracts,
        JsonElement? targetInputSchema)
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

        if (targetInputSchema is not null
            && validatedSteps.TryGetValue(reference.StepId, out var sourceStep)
            && resolvedOutputContracts.TryGetValue(reference.StepId, out var sourceOutputContract))
        {
            ValidateBindingCompatibilityOrThrow(stepId, inputName, binding, reference, sourceStep, sourceOutputContract, targetInputSchema.Value);
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

    private static void ValidateBindingCompatibilityOrThrow(
        string stepId,
        string inputName,
        PlanInputBindingSpec binding,
        ParsedStepReference reference,
        PlanStep sourceStep,
        ResolvedPlanStepOutputContract sourceOutputContract,
        JsonElement targetInputSchema)
    {
        if (sourceOutputContract.FinalSchema is not { } finalSchema)
            return;

        var sourceSchema = TryResolveReferenceSchema(finalSchema, reference);
        if (sourceSchema is null)
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' input '{inputName}' binds from '{binding.From}', but that path does not exist in the output schema of step '{sourceStep.Id}'.");
        }

        if (binding.Mode == PlanInputBindingMode.Map)
        {
            if (!PlanStepOutputContractResolver.SchemaDefinesArray(sourceSchema.Value)
                || !sourceSchema.Value.TryGetProperty("items", out var itemsSchema))
            {
                return;
            }

            sourceSchema = itemsSchema.Clone();
        }

        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(sourceSchema.Value, out var sourceTypes)
            || !PlanStepOutputContractResolver.TryGetSchemaTypes(targetInputSchema, out var targetTypes))
        {
            return;
        }

        if (sourceTypes.Contains("null", StringComparer.Ordinal)
            && !targetTypes.Contains("null", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Step '{stepId}' input '{inputName}' binds from '{binding.From}', which may resolve to null, but the target tool input requires a non-null value.");
        }

        var nonNullSourceTypes = sourceTypes
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .ToList();
        if (nonNullSourceTypes.Count == 0)
            return;

        var incompatibleSourceTypes = nonNullSourceTypes
            .Where(sourceType => !TargetAcceptsSourceType(targetTypes, sourceType))
            .ToList();
        if (incompatibleSourceTypes.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Step '{stepId}' input '{inputName}' binds from '{binding.From}', but source step '{sourceStep.Id}' produces {string.Join("|", nonNullSourceTypes)} while the target tool input expects {string.Join("|", targetTypes)}.");
    }

    private static JsonElement? TryResolveReferenceSchema(JsonElement currentSchema, ParsedStepReference reference)
    {
        var current = currentSchema;
        foreach (var segment in reference.Segments)
        {
            switch (segment.Kind)
            {
                case StepReferenceSegmentKind.Property:
                    current = ResolvePropertySchema(current, segment.PropertyName!);
                    break;
                case StepReferenceSegmentKind.ArrayAny:
                    current = ResolveArrayItemSchema(current, keepArrayShape: true);
                    break;
                case StepReferenceSegmentKind.ArrayIndex:
                    current = ResolveArrayItemSchema(current, keepArrayShape: false);
                    break;
                default:
                    return null;
            }

            if (current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return null;
        }

        return current.Clone();
    }

    private static JsonElement ResolvePropertySchema(JsonElement currentSchema, string propertyName)
    {
        if (PlanStepOutputContractResolver.SchemaDefinesArray(currentSchema)
            && currentSchema.TryGetProperty("items", out var itemSchema))
        {
            var projectedItemSchema = ResolvePropertySchema(itemSchema, propertyName);
            if (projectedItemSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return default;

            return WrapArraySchema(projectedItemSchema);
        }

        if (!currentSchema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object
            || !propertiesElement.TryGetProperty(propertyName, out var propertySchema))
        {
            return default;
        }

        return propertySchema.Clone();
    }

    private static JsonElement ResolveArrayItemSchema(JsonElement currentSchema, bool keepArrayShape)
    {
        if (!PlanStepOutputContractResolver.SchemaDefinesArray(currentSchema)
            || !currentSchema.TryGetProperty("items", out var itemsSchema))
        {
            return default;
        }

        return keepArrayShape
            ? currentSchema.Clone()
            : itemsSchema.Clone();
    }

    private static JsonElement WrapArraySchema(JsonElement itemSchema) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "array",
            ["items"] = JsonNode.Parse(itemSchema.GetRawText())
        });

    private static bool TargetAcceptsSourceType(IReadOnlyList<string> targetTypes, string sourceType) =>
        targetTypes.Contains(sourceType, StringComparer.Ordinal)
        || (string.Equals(sourceType, "integer", StringComparison.Ordinal)
            && targetTypes.Contains("number", StringComparer.Ordinal));
}
