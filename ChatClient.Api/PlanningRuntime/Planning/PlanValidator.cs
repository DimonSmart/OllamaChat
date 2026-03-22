using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static partial class PlanValidator
{
    public static bool TryValidate(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools,
        out PlanValidationIssue? issue)
        => TryValidate(plan, tools, callableAgents: null, out issue);

    public static bool TryValidate(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools,
        IReadOnlyCollection<PlanningCallableAgentDescriptor>? callableAgents,
        out PlanValidationIssue? issue)
    {
        try
        {
            ValidateCore(plan, tools, callableAgents);
            issue = null;
            return true;
        }
        catch (PlanValidationException ex)
        {
            issue = ex.Issue;
            return false;
        }
    }

    public static void ValidateOrThrow(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools = null)
        => ValidateOrThrow(plan, tools, callableAgents: null);

    public static void ValidateOrThrow(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools,
        IReadOnlyCollection<PlanningCallableAgentDescriptor>? callableAgents)
    {
        if (TryValidate(plan, tools, callableAgents, out var issue))
            return;

        throw new InvalidOperationException(issue!.Message, new PlanValidationException(issue));
    }

    private static void ValidateCore(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools,
        IReadOnlyCollection<PlanningCallableAgentDescriptor>? callableAgents)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
            throw CreateIssue("plan_goal_missing", "Plan.goal is required.", path: "goal");

        if (plan.Steps.Count == 0)
            throw CreateIssue("plan_steps_empty", "Plan.steps must contain at least one step.", path: "steps");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var validatedSteps = new Dictionary<string, PlanStep>(StringComparer.Ordinal);
        var resolvedOutputContracts = new Dictionary<string, ResolvedPlanStepOutputContract>(StringComparer.Ordinal);
        var knownTools = tools?
            .ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
        var knownAgents = callableAgents?
            .ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                throw CreateIssue("step_id_missing", "Each step must have an id.", path: "steps[].id");

            if (!seenIds.Add(step.Id))
                throw CreateIssue("step_id_duplicate", $"Duplicate step id '{step.Id}'.", stepId: step.Id, path: $"{step.Id}.id");

            var hasTool = !string.IsNullOrWhiteSpace(step.Tool);
            var hasLlm = !string.IsNullOrWhiteSpace(step.Llm);
            var hasAgent = !string.IsNullOrWhiteSpace(step.Agent);
            var selectedKindCount = (hasTool ? 1 : 0) + (hasLlm ? 1 : 0) + (hasAgent ? 1 : 0);
            if (selectedKindCount != 1)
            {
                throw CreateIssue(
                    "step_kind_invalid",
                    $"Step '{step.Id}' must have exactly one of 'tool', 'llm', or 'agent'.",
                    stepId: step.Id);
            }

            if (!IsValidStatus(step.Status))
                throw CreateIssue("step_status_invalid", $"Step '{step.Id}' has invalid status '{step.Status}'.", stepId: step.Id, actual: step.Status);

            if (step.In.Count == 0)
                throw CreateIssue("step_inputs_missing", $"Step '{step.Id}' must declare its inputs in 'in'.", stepId: step.Id, path: $"{step.Id}.in");

            AppToolDescriptor? toolMetadata = null;
            Dictionary<string, JsonElement>? toolInputProperties = null;
            if (hasTool && knownTools is not null)
            {
                if (!knownTools.TryGetValue(step.Tool!, out toolMetadata))
                    throw CreateIssue("tool_unknown", $"Step '{step.Id}' references unknown tool '{step.Tool}'.", stepId: step.Id, toolName: step.Tool);

                toolInputProperties = ValidateToolInputs(step, toolMetadata);
            }

            if (hasAgent && knownAgents is not null && !knownAgents.ContainsKey(step.Agent!))
            {
                throw CreateIssue(
                    "agent_unknown",
                    $"Step '{step.Id}' references unknown callable agent '{step.Agent}'.",
                    stepId: step.Id,
                    actual: step.Agent);
            }

            foreach (var input in step.In)
            {
                JsonElement? targetInputSchema = null;
                if (toolInputProperties is not null && toolInputProperties.TryGetValue(input.Key, out var propertySchema))
                    targetInputSchema = propertySchema;
                ValidateInputOrThrow(
                    step,
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
                    throw CreateIssue("llm_system_prompt_missing", $"LLM step '{step.Id}' must provide systemPrompt.", stepId: step.Id, path: $"{step.Id}.systemPrompt");
                if (string.IsNullOrWhiteSpace(step.UserPrompt))
                    throw CreateIssue("llm_user_prompt_missing", $"LLM step '{step.Id}' must provide userPrompt.", stepId: step.Id, path: $"{step.Id}.userPrompt");
                if (ContainsPromptRef(step.SystemPrompt!))
                    throw CreateIssue("llm_system_prompt_ref", $"LLM step '{step.Id}' must not embed step refs inside systemPrompt.", stepId: step.Id, path: $"{step.Id}.systemPrompt");
                if (ContainsPromptRef(step.UserPrompt!))
                    throw CreateIssue("llm_user_prompt_ref", $"LLM step '{step.Id}' must not embed step refs inside userPrompt.", stepId: step.Id, path: $"{step.Id}.userPrompt");
                if (ContainsTemplatePlaceholder(step.SystemPrompt!) || ContainsTemplatePlaceholder(step.UserPrompt!))
                {
                    throw CreateIssue(
                        "llm_prompt_template_placeholder",
                        $"LLM step '{step.Id}' must not contain unresolved template placeholders like '{{name}}', '{{{{name}}}}', '[[name]]', '<<name>>', or '${{name}}' in prompts.",
                        stepId: step.Id);
                }
            }
            else if (hasAgent)
            {
                if (!string.IsNullOrWhiteSpace(step.SystemPrompt))
                {
                    throw CreateIssue(
                        "agent_system_prompt_forbidden",
                        $"Saved-agent step '{step.Id}' must not provide systemPrompt.",
                        stepId: step.Id,
                        path: $"{step.Id}.systemPrompt");
                }

                if (string.IsNullOrWhiteSpace(step.UserPrompt))
                {
                    throw CreateIssue(
                        "agent_user_prompt_missing",
                        $"Saved-agent step '{step.Id}' must provide userPrompt.",
                        stepId: step.Id,
                        path: $"{step.Id}.userPrompt");
                }

                if (ContainsPromptRef(step.UserPrompt!))
                {
                    throw CreateIssue(
                        "agent_user_prompt_ref",
                        $"Saved-agent step '{step.Id}' must not embed step refs inside userPrompt.",
                        stepId: step.Id,
                        path: $"{step.Id}.userPrompt");
                }

                if (ContainsTemplatePlaceholder(step.UserPrompt!))
                {
                    throw CreateIssue(
                        "agent_prompt_template_placeholder",
                        $"Saved-agent step '{step.Id}' must not contain unresolved template placeholders like '{{name}}', '{{{{name}}}}', '[[name]]', '<<name>>', or '${{name}}' in prompts.",
                        stepId: step.Id);
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
            throw CreateIssue(
                "tool_schema_invalid",
                $"Tool '{toolMetadata.QualifiedName}' has invalid input schema. It must define an object 'properties' map.",
                toolName: toolMetadata.QualifiedName);
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
                    throw CreateIssue(
                        "tool_input_missing_required",
                        $"Tool step '{step.Id}' is missing required input '{propertyName}' for tool '{toolMetadata.QualifiedName}'.",
                        stepId: step.Id,
                        inputName: propertyName,
                        toolName: toolMetadata.QualifiedName);
                }
            }
        }

        foreach (var input in step.In)
        {
            if (!properties.TryGetValue(input.Key, out var propertySchema))
            {
                throw CreateIssue(
                    "tool_input_unknown",
                    $"Tool step '{step.Id}' passes unknown input '{input.Key}' to tool '{toolMetadata.QualifiedName}'.",
                    stepId: step.Id,
                    inputName: input.Key,
                    toolName: toolMetadata.QualifiedName);
            }

            if (PlanInputBindingSyntax.TryGetLegacyStringReference(input.Value, out _))
                continue;

            if (PlanInputBindingSyntax.TryParseBinding(input.Value, out _, out _))
                continue;

            var issues = ToolInputSchemaValidator.ValidateLiteralInput(input.Value, propertySchema);
            if (issues.Count == 0)
                continue;

            throw CreateIssue(
                "tool_input_schema_mismatch",
                $"Tool step '{step.Id}' input '{input.Key}' does not match tool schema: {string.Join(" ", issues.Select(issue => issue.Message))}",
                stepId: step.Id,
                inputName: input.Key,
                toolName: toolMetadata.QualifiedName);
        }

        var wholeInputSchemaIssues = ToolInputSchemaValidator.ValidateDraftInput(
            JsonSerializer.SerializeToNode(step.In),
            toolMetadata.InputSchema);
        if (wholeInputSchemaIssues.Count > 0)
        {
            throw CreateIssue(
                "tool_input_schema_mismatch",
                $"Tool step '{step.Id}' does not match tool schema: {string.Join(" ", wholeInputSchemaIssues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal))}",
                stepId: step.Id,
                toolName: toolMetadata.QualifiedName);
        }

        return properties;
    }

    private static void ValidateInputOrThrow(
        PlanStep step,
        string inputName,
        JsonNode? value,
        HashSet<string> knownStepIds,
        IReadOnlyDictionary<string, PlanStep> validatedSteps,
        IReadOnlyDictionary<string, ResolvedPlanStepOutputContract> resolvedOutputContracts,
        JsonElement? targetInputSchema)
    {
        var stepId = step.Id;
        if (PlanInputBindingSyntax.TryGetLegacyStringReference(value, out var legacyReference))
        {
            throw CreateIssue(
                "binding_legacy_ref",
                $"Step '{stepId}' input '{inputName}' uses legacy string ref syntax '{legacyReference}'. Use a binding object like {{\"from\":\"{legacyReference}\",\"mode\":\"value\"}}.",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: legacyReference);
        }

        if (!PlanInputBindingSyntax.TryParseBinding(value, out var binding, out var bindingError))
            return;

        if (!string.IsNullOrWhiteSpace(bindingError))
            throw CreateIssue("binding_invalid", $"Step '{stepId}' has invalid binding in input '{inputName}': {bindingError}", stepId: stepId, inputName: inputName);

        if (!PlanInputBindingSyntax.TryParseReference(binding!.From, out var reference, out var refError))
        {
            throw CreateIssue(
                "binding_ref_invalid",
                $"Step '{stepId}' has invalid ref syntax in input '{inputName}': '{binding.From}'. {refError}",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: binding.From);
        }

        if (!knownStepIds.Contains(reference!.StepId))
        {
            throw CreateIssue(
                "binding_ref_future_step",
                $"Step '{stepId}' references '{binding.From}' before step '{reference.StepId}' is available.",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: binding.From,
                sourceStepId: reference.StepId);
        }

        if (targetInputSchema is not null
            && validatedSteps.TryGetValue(reference.StepId, out var sourceStep)
            && resolvedOutputContracts.TryGetValue(reference.StepId, out var sourceOutputContract))
        {
            var sourceSchema = ResolveBoundSourceSchemaOrThrow(stepId, inputName, binding, reference, sourceStep, sourceOutputContract);

            if (!string.IsNullOrWhiteSpace(binding.Type))
                ValidateBindingDeclaredTypeOrThrow(stepId, inputName, binding, sourceSchema);

            ValidateBindingCompatibilityOrThrow(stepId, inputName, binding, sourceStep, sourceSchema, targetInputSchema.Value);
        }
        else if (!string.IsNullOrWhiteSpace(binding.Type)
            && validatedSteps.TryGetValue(reference.StepId, out var typedSourceStep)
            && resolvedOutputContracts.TryGetValue(reference.StepId, out var typedSourceOutputContract))
        {
            var sourceSchema = ResolveBoundSourceSchemaOrThrow(stepId, inputName, binding, reference, typedSourceStep, typedSourceOutputContract);
            ValidateBindingDeclaredTypeOrThrow(stepId, inputName, binding, sourceSchema);
        }
    }

    private static void ValidateOutputContractOrThrow(PlanStep step, AppToolDescriptor? toolMetadata)
    {
        var issues = PlanStepOutputContractResolver.ValidateContractDefinition(step, toolMetadata);
        if (issues.Count == 0)
            return;

        throw CreateIssue("step_out_contract_invalid", $"Step '{step.Id}' has invalid out contract: {string.Join(" ", issues)}", stepId: step.Id, path: $"{step.Id}.out");
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
        PlanStep sourceStep,
        JsonElement sourceSchema,
        JsonElement targetInputSchema)
    {
        var compatibilityIssues = new List<string>();
        ValidateSchemaCompatibility(sourceSchema, targetInputSchema, inputName, compatibilityIssues);
        if (compatibilityIssues.Count == 0)
            return;

        throw CreateIssue(
            "binding_tool_schema_mismatch",
            $"Step '{stepId}' input '{inputName}' binds from '{binding.From}', but the bound source schema is incompatible with the target tool input: {string.Join(" ", compatibilityIssues)}",
            stepId: stepId,
            inputName: inputName,
            bindingFrom: binding.From,
            sourceStepId: sourceStep.Id,
            expected: DescribeSchemaShape(targetInputSchema),
            actual: DescribeSchemaShape(sourceSchema));
    }

    private static void ValidateSchemaCompatibility(
        JsonElement sourceSchema,
        JsonElement targetSchema,
        string path,
        List<string> issues)
    {
        if (TryGetCompositeVariants(targetSchema, "oneOf", out var oneOfVariants)
            || TryGetCompositeVariants(targetSchema, "anyOf", out oneOfVariants))
        {
            foreach (var variant in oneOfVariants)
            {
                var variantIssues = new List<string>();
                ValidateSchemaCompatibility(sourceSchema, variant, path, variantIssues);
                if (variantIssues.Count == 0)
                    return;
            }

            issues.Add($"Source schema at '{path}' does not satisfy any allowed input schema alternative.");
            return;
        }

        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(sourceSchema, out var sourceTypes)
            || !PlanStepOutputContractResolver.TryGetSchemaTypes(targetSchema, out var targetTypes))
        {
            return;
        }

        if (sourceTypes.Contains("null", StringComparer.Ordinal)
            && !targetTypes.Contains("null", StringComparer.Ordinal))
        {
            issues.Add($"Input '{path}' may resolve to null, but the target tool input requires a non-null value.");
            return;
        }

        var nonNullSourceTypes = sourceTypes
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .ToList();
        if (nonNullSourceTypes.Count == 0)
            return;

        var incompatibleSourceTypes = nonNullSourceTypes
            .Where(sourceType => !TargetAcceptsSourceType(targetTypes, sourceType))
            .ToList();
        if (incompatibleSourceTypes.Count > 0)
        {
            issues.Add($"Input '{path}' expects {string.Join("|", targetTypes)}, but the bound source schema produces {string.Join("|", incompatibleSourceTypes)}.");
            return;
        }

        if (targetTypes.Contains("object", StringComparer.Ordinal)
            && nonNullSourceTypes.Contains("object", StringComparer.Ordinal))
        {
            ValidateObjectSchemaCompatibility(sourceSchema, targetSchema, path, issues);
        }

        if (targetTypes.Contains("array", StringComparer.Ordinal)
            && nonNullSourceTypes.Contains("array", StringComparer.Ordinal)
            && sourceSchema.TryGetProperty("items", out var sourceItems)
            && targetSchema.TryGetProperty("items", out var targetItems))
        {
            ValidateSchemaCompatibility(sourceItems, targetItems, $"{path}[]", issues);
        }
    }

    private static void ValidateObjectSchemaCompatibility(
        JsonElement sourceSchema,
        JsonElement targetSchema,
        string path,
        List<string> issues)
    {
        if (!targetSchema.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.Array
            || !TryGetPropertySchemas(sourceSchema, out var sourceProperties)
            || !TryGetPropertySchemas(targetSchema, out var targetProperties))
        {
            return;
        }

        foreach (var requiredProperty in requiredElement.EnumerateArray())
        {
            var propertyName = requiredProperty.GetString();
            if (string.IsNullOrWhiteSpace(propertyName))
                continue;

            if (!targetProperties.TryGetValue(propertyName, out var targetPropertySchema))
                continue;

            if (!sourceProperties.TryGetValue(propertyName, out var sourcePropertySchema))
            {
                issues.Add($"Required property '{propertyName}' is not guaranteed by the bound source schema at '{path}'.");
                continue;
            }

            ValidateSchemaCompatibility(sourcePropertySchema, targetPropertySchema, $"{path}.{propertyName}", issues);
        }
    }

    private static bool TryGetPropertySchemas(
        JsonElement schema,
        out IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (schema.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object)
        {
            properties = propertiesElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return true;
        }

        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryGetCompositeVariants(
        JsonElement schema,
        string propertyName,
        out IReadOnlyList<JsonElement> variants)
    {
        variants = Array.Empty<JsonElement>();
        if (!schema.TryGetProperty(propertyName, out var variantsElement)
            || variantsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        variants = variantsElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => item.Clone())
            .ToArray();

        return variants.Count > 0;
    }

    private static JsonElement ResolveBoundSourceSchemaOrThrow(
        string stepId,
        string inputName,
        PlanInputBindingSpec binding,
        ParsedStepReference reference,
        PlanStep sourceStep,
        ResolvedPlanStepOutputContract sourceOutputContract)
    {
        if (sourceOutputContract.FinalSchema is not { } finalSchema)
            return default;

        var sourceSchema = TryResolveReferenceSchema(finalSchema, reference);
        if (sourceSchema is null)
        {
            throw CreateIssue(
                "binding_path_missing",
                $"Step '{stepId}' input '{inputName}' binds from '{binding.From}', but that path does not exist in the output schema of step '{sourceStep.Id}'.",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: binding.From,
                sourceStepId: sourceStep.Id);
        }

        if (binding.Mode != PlanInputBindingMode.Map)
            return sourceSchema.Value.Clone();

        if (!PlanStepOutputContractResolver.SchemaDefinesArray(sourceSchema.Value)
            || !sourceSchema.Value.TryGetProperty("items", out var itemsSchema))
        {
            throw CreateIssue(
                "binding_map_non_array",
                $"Step '{stepId}' input '{inputName}' uses mode='map', but '{binding.From}' does not resolve to an array in the output schema of step '{sourceStep.Id}'.",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: binding.From,
                sourceStepId: sourceStep.Id);
        }

        return itemsSchema.Clone();
    }

    private static void ValidateBindingDeclaredTypeOrThrow(
        string stepId,
        string inputName,
        PlanInputBindingSpec binding,
        JsonElement sourceSchema)
    {
        if (!StepInputTypeValidator.TryParse(binding.Type, out var expectedType, out var typeError) || expectedType is null)
        {
            throw CreateIssue(
                "binding_type_invalid",
                $"Step '{stepId}' input '{inputName}' declares invalid type '{binding.Type}'. {typeError}",
                stepId: stepId,
                inputName: inputName,
                bindingFrom: binding.From,
                actual: binding.Type);
        }

        var issues = StepInputTypeValidator.ValidateSourceSchema(sourceSchema, expectedType, inputName);
        if (issues.Count == 0)
            return;

        var actualShape = DescribeSchemaShape(sourceSchema);

        throw CreateIssue(
            "binding_type_mismatch",
            $"Step '{stepId}' input '{inputName}' declares type '{binding.Type}', but the binding is incompatible: {string.Join(" ", issues.Select(issue => issue.Message))} Bound source schema shape is '{actualShape}'.",
            stepId: stepId,
            inputName: inputName,
            bindingFrom: binding.From,
            expected: binding.Type,
            actual: actualShape);
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

    private static string DescribeSchemaShape(JsonElement schema)
    {
        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var types) || types.Count == 0)
            return "unknown";

        var nonNullTypes = types
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var isNullable = nonNullTypes.Count != types.Count;

        string shape;
        if (nonNullTypes.Count == 0)
        {
            shape = "null";
        }
        else if (nonNullTypes.Count == 1)
        {
            shape = nonNullTypes[0];
            if (string.Equals(shape, "array", StringComparison.Ordinal)
                && schema.TryGetProperty("items", out var itemsSchema))
            {
                shape = $"array<{DescribeSchemaShape(itemsSchema)}>";
            }
        }
        else
        {
            shape = string.Join("|", nonNullTypes);
        }

        return isNullable && !string.Equals(shape, "null", StringComparison.Ordinal)
            ? $"{shape}|null"
            : shape;
    }

    private static bool TargetAcceptsSourceType(IReadOnlyList<string> targetTypes, string sourceType) =>
        targetTypes.Contains(sourceType, StringComparer.Ordinal)
        || (string.Equals(sourceType, "integer", StringComparison.Ordinal)
            && targetTypes.Contains("number", StringComparer.Ordinal));

    private static PlanValidationException CreateIssue(
        string code,
        string message,
        string? stepId = null,
        string? inputName = null,
        string? toolName = null,
        string? bindingFrom = null,
        string? sourceStepId = null,
        string? path = null,
        string? expected = null,
        string? actual = null) =>
        new(new PlanValidationIssue(
            code,
            message,
            StepId: stepId,
            InputName: inputName,
            ToolName: toolName,
            BindingFrom: bindingFrom,
            SourceStepId: sourceStepId,
            Path: path,
            Expected: expected,
            Actual: actual));
}
