using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanNormalizer
{
    public static PlanDefinition Normalize(
        PlanDefinition plan,
        IReadOnlyCollection<AppToolDescriptor>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var toolMap = tools?.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Steps.Count; index++)
            plan.Steps[index] = NormalizeStep(plan.Steps[index], toolMap);

        return plan;
    }

    private static PlanStep NormalizeStep(
        PlanStep step,
        IReadOnlyDictionary<string, AppToolDescriptor>? toolMap)
    {
        var normalizedKind = PlanStepKinds.TryNormalize(step.Kind, out var kind)
            ? kind
            : step.Kind?.Trim() ?? string.Empty;
        var normalizedInputs = NormalizeInputs(step.In);
        var hasMappedInputs = HasMappedInputs(normalizedInputs);
        var normalizedCapabilityId = NormalizeCapabilityId(step.CapabilityId);
        var toolMetadata = ResolveToolMetadata(normalizedKind, normalizedCapabilityId, toolMap);
        var normalizedOut = NormalizeOutputContract(normalizedKind, step.Out, toolMetadata, hasMappedInputs);

        return new PlanStep
        {
            Id = step.Id,
            Kind = normalizedKind,
            CapabilityId = normalizedCapabilityId,
            SystemPrompt = step.SystemPrompt,
            UserPrompt = step.UserPrompt,
            In = normalizedInputs,
            Out = normalizedOut,
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
        if (PlanInputBindingSyntax.TryGetLegacyStringReference(value, out var legacyReference)
            && legacyReference is not null)
        {
            return CreateBindingNode(new PlanInputBindingSpec(legacyReference, PlanInputBindingMode.Value));
        }

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

    private static bool HasMappedInputs(IReadOnlyDictionary<string, JsonNode?> inputs) =>
        inputs.Values.Any(value =>
            PlanInputBindingSyntax.TryParseBinding(value, out var bindingExpression, out var error)
            && string.IsNullOrWhiteSpace(error)
            && bindingExpression is not null
            && PlanInputBindingSyntax.EnumerateBindings(bindingExpression)
                .Any(binding => binding.Mode == PlanInputBindingMode.Map));

    private static string? NormalizeCapabilityId(string? capabilityId)
    {
        var trimmed = capabilityId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return trimmed;
    }

    private static AppToolDescriptor? ResolveToolMetadata(
        string normalizedKind,
        string? capabilityId,
        IReadOnlyDictionary<string, AppToolDescriptor>? toolMap)
    {
        if (!string.Equals(normalizedKind, PlanStepKinds.Tool, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(capabilityId)
            || toolMap is null)
        {
            return null;
        }

        toolMap.TryGetValue(capabilityId, out var toolMetadata);
        return toolMetadata;
    }

    private static PlanStepOutputContract? NormalizeOutputContract(
        string normalizedKind,
        PlanStepOutputContract? explicitContract,
        AppToolDescriptor? toolMetadata,
        bool hasMappedInputs)
    {
        if (string.Equals(normalizedKind, PlanStepKinds.Tool, StringComparison.Ordinal))
            return null;

        if (!string.Equals(normalizedKind, PlanStepKinds.Llm, StringComparison.Ordinal)
            && !string.Equals(normalizedKind, PlanStepKinds.Agent, StringComparison.Ordinal))
        {
            return CloneOutputContract(explicitContract);
        }

        var schema = explicitContract?.Schema?.Clone();
        var format = NormalizeFormat(explicitContract, schema, toolMetadata);
        if (format == PlanStepOutputFormats.Json && schema is null)
            schema = CreateDefaultJsonSchema();

        if (format == PlanStepOutputFormats.String
            && schema is { } stringSchema
            && !PlanStepOutputContractResolver.SchemaAcceptsType(stringSchema, JsonValueKind.String))
        {
            format = PlanStepOutputFormats.Json;
            schema = stringSchema.Clone();
        }

        var callSchema = ResolveCallSchema(format, schema, toolMetadata);
        var aggregate = NormalizeAggregate(explicitContract, hasMappedInputs, callSchema);

        return new PlanStepOutputContract
        {
            Format = format,
            Aggregate = aggregate,
            Schema = format == PlanStepOutputFormats.String && explicitContract?.Schema is null
                ? null
                : schema?.Clone()
        };
    }

    private static PlanStepOutputContract? CloneOutputContract(PlanStepOutputContract? contract)
    {
        if (contract is null)
            return null;

        return new PlanStepOutputContract
        {
            Format = contract.Format,
            Aggregate = contract.Aggregate,
            Schema = contract.Schema?.Clone()
        };
    }

    private static string NormalizeFormat(
        PlanStepOutputContract? explicitContract,
        JsonElement? schema,
        AppToolDescriptor? toolMetadata)
    {
        if (explicitContract is not null
            && PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var explicitFormat))
        {
            if (explicitFormat == PlanStepOutputFormats.String
                && schema is { } stringSchema
                && !PlanStepOutputContractResolver.SchemaAcceptsType(stringSchema, JsonValueKind.String))
            {
                return PlanStepOutputFormats.Json;
            }

            return explicitFormat;
        }

        if (schema is { } inferredSchema)
        {
            if (PlanStepOutputContractResolver.TryGetSchemaTypes(inferredSchema, out var schemaTypes))
            {
                var nonNullTypes = schemaTypes
                    .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (nonNullTypes.Length == 1 && string.Equals(nonNullTypes[0], "string", StringComparison.Ordinal))
                    return PlanStepOutputFormats.String;

                if (nonNullTypes.Length > 0)
                    return PlanStepOutputFormats.Json;
            }

            return PlanStepOutputFormats.Json;
        }

        if (toolMetadata?.OutputSchema is { } toolSchema
            && PlanStepOutputContractResolver.TryGetSchemaTypes(toolSchema, out var toolTypes)
            && toolTypes.Count > 0)
        {
            var nonNullTypes = toolTypes
                .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (nonNullTypes.Length == 1 && string.Equals(nonNullTypes[0], "string", StringComparison.Ordinal))
                return PlanStepOutputFormats.String;
        }

        return PlanStepOutputFormats.String;
    }

    private static string NormalizeAggregate(
        PlanStepOutputContract? explicitContract,
        bool hasMappedInputs,
        JsonElement? callSchema)
    {
        if (explicitContract is not null
            && PlanStepOutputAggregates.TryNormalize(explicitContract.Aggregate, out var explicitAggregate)
            && !hasMappedInputs
            && string.Equals(explicitAggregate, PlanStepOutputAggregates.Single, StringComparison.Ordinal))
        {
            return explicitAggregate;
        }

        if (!hasMappedInputs)
            return PlanStepOutputAggregates.Single;

        return callSchema is { } schema && PlanStepOutputContractResolver.SchemaDefinesArray(schema)
            ? PlanStepOutputAggregates.Flatten
            : PlanStepOutputAggregates.Collect;
    }

    private static JsonElement? ResolveCallSchema(
        string format,
        JsonElement? schema,
        AppToolDescriptor? toolMetadata)
    {
        if (schema is { } explicitSchema)
            return explicitSchema.Clone();

        if (toolMetadata?.OutputSchema is { } toolSchema)
            return toolSchema.Clone();

        return string.Equals(format, PlanStepOutputFormats.String, StringComparison.Ordinal)
            ? CreateStringSchema()
            : CreateDefaultJsonSchema();
    }

    private static JsonElement CreateStringSchema() =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "string"
        });

    private static JsonElement CreateDefaultJsonSchema() =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object"
        });
}
