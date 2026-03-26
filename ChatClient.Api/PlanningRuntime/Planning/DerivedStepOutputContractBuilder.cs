using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class DerivedStepOutputContractBuilder
{
    private sealed record ConsumerRequirement(
        JsonElement FinalSchema,
        DerivedStepOutputContractSource Source);

    public static IReadOnlyDictionary<string, ResolvedPlanStepOutputContract> Build(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var toolMap = tools?.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
        var requirementsBySourceStep = CollectConsumerRequirements(plan, toolMap);
        var contracts = new Dictionary<string, ResolvedPlanStepOutputContract>(plan.Steps.Count, StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            var toolMetadata = ResolveToolMetadata(step, toolMap);
            requirementsBySourceStep.TryGetValue(step.Id, out var requirements);
            contracts[step.Id] = BuildStepContract(step, toolMetadata, requirements ?? []);
        }

        return contracts;
    }

    private static ResolvedPlanStepOutputContract BuildStepContract(
        PlanStep step,
        AppToolDescriptor? toolMetadata,
        IReadOnlyList<ConsumerRequirement> requirements)
    {
        var isMapped = PlanStepOutputContractResolver.HasMappedInputs(step);
        var format = ResolveFormat(step, toolMetadata);

        if (PlanStepKinds.IsTool(step))
            return BuildToolContract(format, isMapped, toolMetadata);

        if (step.Out?.Schema is { } explicitSchema)
            return BuildExplicitContract(format, isMapped, explicitSchema);

        if (string.Equals(format, PlanStepOutputFormats.String, StringComparison.OrdinalIgnoreCase))
        {
            var stringSchema = PlanStepOutputContractResolver.CreateStringSchema();
            return BuildDerivedContract(
                format,
                isMapped,
                BuildFinalSchema(isMapped, stringSchema),
                DerivedStepOutputContractSource.ExplicitOutputSchema);
        }

        if (requirements.Count == 0)
            return CreateOpaqueContract(format, isMapped);

        var requirementSchemas = requirements
            .Select(static requirement => requirement.FinalSchema.Clone())
            .ToArray();
        if (!DerivedContractSchemaMerger.TryMergeAll(requirementSchemas, out var mergedFinalSchema))
            return CreateOpaqueContract(format, isMapped);

        var source = requirements.Any(static requirement => requirement.Source == DerivedStepOutputContractSource.DerivedFromToolConsumer)
            ? DerivedStepOutputContractSource.DerivedFromToolConsumer
            : DerivedStepOutputContractSource.DerivedFromBindingPath;

        return BuildDerivedContract(format, isMapped, mergedFinalSchema, source);
    }

    private static ResolvedPlanStepOutputContract BuildToolContract(
        string format,
        bool isMapped,
        AppToolDescriptor? toolMetadata)
    {
        if (toolMetadata?.OutputSchema is not { } toolSchema)
            return CreateOpaqueContract(format, isMapped);

        var finalSchema = BuildFinalSchemaForMappedCalls(isMapped, toolSchema);
        return new ResolvedPlanStepOutputContract(
            Format: format,
            IsMapped: isMapped,
            CallSchema: toolSchema.Clone(),
            FinalSchema: finalSchema,
            Source: DerivedStepOutputContractSource.ToolOutputSchema,
            IsOpaque: false);
    }

    private static ResolvedPlanStepOutputContract BuildExplicitContract(
        string format,
        bool isMapped,
        JsonElement explicitSchema)
    {
        var finalSchema = isMapped && PlanStepOutputContractResolver.SchemaDefinesArray(explicitSchema)
            ? explicitSchema.Clone()
            : BuildFinalSchema(isMapped, explicitSchema);
        return new ResolvedPlanStepOutputContract(
            Format: format,
            IsMapped: isMapped,
            CallSchema: explicitSchema.Clone(),
            FinalSchema: finalSchema,
            Source: DerivedStepOutputContractSource.ExplicitOutputSchema,
            IsOpaque: false);
    }

    private static ResolvedPlanStepOutputContract BuildDerivedContract(
        string format,
        bool isMapped,
        JsonElement mergedFinalSchema,
        DerivedStepOutputContractSource source)
    {
        var finalSchema = isMapped && !PlanStepOutputContractResolver.SchemaDefinesArray(mergedFinalSchema)
            ? PlanStepOutputContractResolver.CreateArraySchema(mergedFinalSchema)
            : mergedFinalSchema.Clone();

        var callSchema = isMapped && PlanStepOutputContractResolver.TryGetArrayItemSchema(finalSchema, out var itemSchema)
            ? itemSchema
            : finalSchema.Clone();

        return new ResolvedPlanStepOutputContract(
            Format: format,
            IsMapped: isMapped,
            CallSchema: callSchema,
            FinalSchema: finalSchema,
            Source: source,
            IsOpaque: false);
    }

    private static ResolvedPlanStepOutputContract CreateOpaqueContract(string format, bool isMapped) =>
        new(
            Format: format,
            IsMapped: isMapped,
            CallSchema: null,
            FinalSchema: null,
            Source: DerivedStepOutputContractSource.Opaque,
            IsOpaque: true);

    private static Dictionary<string, List<ConsumerRequirement>> CollectConsumerRequirements(
        PlanDefinition plan,
        IReadOnlyDictionary<string, AppToolDescriptor>? toolMap)
    {
        var requirementsBySourceStep = new Dictionary<string, List<ConsumerRequirement>>(StringComparer.Ordinal);

        foreach (var consumerStep in plan.Steps)
        {
            var targetToolSchemas = ResolveToolInputSchemas(consumerStep, toolMap);

            foreach (var input in consumerStep.In)
            {
                if (!PlanInputBindingSyntax.TryParseBinding(input.Value, out var bindingExpression, out var bindingError)
                    || !string.IsNullOrWhiteSpace(bindingError)
                    || bindingExpression is null)
                {
                    continue;
                }

                JsonElement? targetToolSchema = targetToolSchemas.TryGetValue(input.Key, out var propertySchema)
                    ? propertySchema
                    : null;
                switch (bindingExpression)
                {
                    case PlanInputBindingSpec binding:
                        AddConsumerRequirement(
                            requirementsBySourceStep,
                            binding,
                            BuildResolvedInputSchema(binding.Mode, targetToolSchema, forceArray: false),
                            targetToolSchema is null
                                ? DerivedStepOutputContractSource.DerivedFromBindingPath
                                : DerivedStepOutputContractSource.DerivedFromToolConsumer);
                        break;
                    case PlanInputConcatBindingSpec concatBinding:
                        foreach (var sourceBinding in concatBinding.Concat)
                        {
                            AddConsumerRequirement(
                                requirementsBySourceStep,
                                sourceBinding,
                                BuildResolvedInputSchema(sourceBinding.Mode, targetToolSchema, forceArray: true),
                                targetToolSchema is null
                                    ? DerivedStepOutputContractSource.DerivedFromBindingPath
                                    : DerivedStepOutputContractSource.DerivedFromToolConsumer);
                        }

                        break;
                }
            }
        }

        return requirementsBySourceStep;
    }

    private static void AddConsumerRequirement(
        Dictionary<string, List<ConsumerRequirement>> requirementsBySourceStep,
        PlanInputBindingSpec binding,
        JsonElement resolvedInputSchema,
        DerivedStepOutputContractSource source)
    {
        if (!PlanInputBindingSyntax.TryParseReference(binding.From, out var reference, out _))
            return;

        var requirement = BuildSourceRequirement(reference!, resolvedInputSchema);
        if (!requirementsBySourceStep.TryGetValue(reference!.StepId, out var list))
        {
            list = [];
            requirementsBySourceStep[reference.StepId] = list;
        }

        list.Add(new ConsumerRequirement(requirement, source));
    }

    private static JsonElement BuildResolvedInputSchema(
        PlanInputBindingMode mode,
        JsonElement? targetToolSchema,
        bool forceArray)
    {
        if (targetToolSchema is { } schema)
        {
            var resolved = schema.Clone();
            if (forceArray)
                return PlanStepOutputContractResolver.SchemaDefinesArray(resolved)
                    ? resolved
                    : PlanStepOutputContractResolver.CreateArraySchema(resolved);

            return mode == PlanInputBindingMode.Map
                ? PlanStepOutputContractResolver.CreateArraySchema(resolved)
                : resolved;
        }

        if (forceArray || mode == PlanInputBindingMode.Map)
            return PlanStepOutputContractResolver.CreateArraySchema(PlanStepOutputContractResolver.CreateOpaqueSchema());

        return PlanStepOutputContractResolver.CreateOpaqueSchema();
    }

    private static JsonElement BuildSourceRequirement(
        ParsedStepReference reference,
        JsonElement resolvedInputSchema)
    {
        var current = resolvedInputSchema.Clone();
        for (var index = reference.Segments.Count - 1; index >= 0; index--)
        {
            var segment = reference.Segments[index];
            var outerSegment = index > 0 ? reference.Segments[index - 1] : null;

            current = segment.Kind switch
            {
                StepReferenceSegmentKind.Property when outerSegment?.Kind == StepReferenceSegmentKind.ArrayAny =>
                    WrapProjectedPropertySchema(segment.PropertyName!, current),
                StepReferenceSegmentKind.Property =>
                    WrapPropertySchema(segment.PropertyName!, current),
                StepReferenceSegmentKind.ArrayAny or StepReferenceSegmentKind.ArrayIndex =>
                    EnsureArraySchema(current),
                _ => current
            };
        }

        return current;
    }

    private static JsonElement WrapProjectedPropertySchema(string propertyName, JsonElement current)
    {
        var propertySchema = PlanStepOutputContractResolver.TryGetArrayItemSchema(current, out var itemSchema)
            ? itemSchema
            : current.Clone();

        return WrapPropertySchema(propertyName, propertySchema);
    }

    private static JsonElement WrapPropertySchema(string propertyName, JsonElement propertySchema) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [propertyName] = JsonNode.Parse(propertySchema.GetRawText())
            },
            ["required"] = new JsonArray(propertyName)
        });

    private static JsonElement EnsureArraySchema(JsonElement current) =>
        PlanStepOutputContractResolver.SchemaDefinesArray(current)
            ? current.Clone()
            : PlanStepOutputContractResolver.CreateArraySchema(current);

    private static JsonElement BuildFinalSchema(bool isMapped, JsonElement schema) =>
        isMapped
            ? PlanStepOutputContractResolver.CreateArraySchema(schema)
            : schema.Clone();

    private static JsonElement BuildFinalSchemaForMappedCalls(bool isMapped, JsonElement callSchema)
    {
        if (!isMapped)
            return callSchema.Clone();

        if (PlanStepOutputContractResolver.TryGetArrayItemSchema(callSchema, out var itemSchema))
            return PlanStepOutputContractResolver.CreateArraySchema(itemSchema);

        return PlanStepOutputContractResolver.CreateArraySchema(callSchema);
    }

    private static string ResolveFormat(PlanStep step, AppToolDescriptor? toolMetadata)
    {
        if (!PlanStepKinds.IsTool(step)
            && step.Out is { } explicitContract
            && PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var explicitFormat))
        {
            return explicitFormat;
        }

        if (toolMetadata?.OutputSchema is { } toolOutputSchema
            && PlanStepOutputContractResolver.TryGetSchemaTypes(toolOutputSchema, out var toolTypes))
        {
            var nonNullTypes = toolTypes
                .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (nonNullTypes.Length == 1
                && string.Equals(nonNullTypes[0], "string", StringComparison.Ordinal))
            {
                return PlanStepOutputFormats.String;
            }
        }

        return PlanStepOutputFormats.Json;
    }

    private static AppToolDescriptor? ResolveToolMetadata(
        PlanStep step,
        IReadOnlyDictionary<string, AppToolDescriptor>? toolMap)
    {
        if (!PlanStepKinds.IsTool(step)
            || string.IsNullOrWhiteSpace(step.CapabilityId)
            || toolMap is null)
        {
            return null;
        }

        toolMap.TryGetValue(step.CapabilityId, out var toolMetadata);
        return toolMetadata;
    }

    private static IReadOnlyDictionary<string, JsonElement> ResolveToolInputSchemas(
        PlanStep consumerStep,
        IReadOnlyDictionary<string, AppToolDescriptor>? toolMap)
    {
        if (!PlanStepKinds.IsTool(consumerStep)
            || string.IsNullOrWhiteSpace(consumerStep.CapabilityId)
            || toolMap is null
            || !toolMap.TryGetValue(consumerStep.CapabilityId, out var toolMetadata)
            || toolMetadata.InputSchema.ValueKind != JsonValueKind.Object
            || !toolMetadata.InputSchema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        return propertiesElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }
}
