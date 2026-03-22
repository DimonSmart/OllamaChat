using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanStepOutputFormats
{
    public const string Json = "json";
    public const string String = "string";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Json;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case Json:
                normalized = Json;
                return true;
            case String:
                normalized = String;
                return true;
            default:
                return false;
        }
    }
}

public static class PlanStepOutputAggregates
{
    public const string Single = "single";
    public const string Collect = "collect";
    public const string Flatten = "flatten";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Single;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case Single:
                normalized = Single;
                return true;
            case Collect:
                normalized = Collect;
                return true;
            case Flatten:
                normalized = Flatten;
                return true;
            default:
                return false;
        }
    }
}

public sealed class PlanStepOutputContract
{
    [JsonRequired]
    [JsonPropertyName("format")]
    public string Format { get; init; } = PlanStepOutputFormats.Json;

    [JsonPropertyName("aggregate")]
    public string Aggregate { get; init; } = PlanStepOutputAggregates.Single;

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }
}

public sealed record ResolvedPlanStepOutputContract(
    string Format,
    string Aggregate,
    JsonElement? CallSchema,
    JsonElement? FinalSchema,
    bool IsExplicit);

public static class PlanStepOutputContractResolver
{
    public static ResolvedPlanStepOutputContract Resolve(
        PlanStep step,
        AppToolDescriptor? toolMetadata,
        bool hasFanOut)
    {
        var explicitContract = step.Out;
        var format = ResolveFormat(explicitContract, toolMetadata);
        var aggregate = ResolveAggregate(explicitContract, hasFanOut);
        var callSchema = ResolveCallSchema(explicitContract, toolMetadata, format);
        var finalSchema = ResolveFinalSchema(callSchema, aggregate);

        return new ResolvedPlanStepOutputContract(
            Format: format,
            Aggregate: aggregate,
            CallSchema: callSchema,
            FinalSchema: finalSchema,
            IsExplicit: explicitContract is not null);
    }

    public static bool HasMappedInputs(PlanStep step) =>
        step.In.Values.Any(value =>
            PlanInputBindingSyntax.TryParseBinding(value, out var binding, out var bindingError)
            && string.IsNullOrWhiteSpace(bindingError)
            && binding!.Mode == PlanInputBindingMode.Map);

    public static IReadOnlyList<string> ValidateContractDefinition(
        PlanStep step,
        AppToolDescriptor? toolMetadata = null)
    {
        var issues = new List<string>();
        var hasFanOut = HasMappedInputs(step);
        var explicitContract = step.Out;

        if ((!string.IsNullOrWhiteSpace(step.Llm) || !string.IsNullOrWhiteSpace(step.Agent)) && explicitContract is null)
        {
            issues.Add("LLM and saved-agent steps must declare an 'out' contract.");
            return issues;
        }

        if (explicitContract is null)
            return issues;

        if (!PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var format))
            issues.Add("out.format must be 'json' or 'string'.");

        if (!PlanStepOutputAggregates.TryNormalize(explicitContract.Aggregate, out var aggregate))
            issues.Add("out.aggregate must be 'single', 'collect', or 'flatten'.");

        if (issues.Count > 0)
            return issues;

        if (hasFanOut && aggregate == PlanStepOutputAggregates.Single)
            issues.Add("Mapped steps must use out.aggregate='collect' or 'flatten'.");

        if (!hasFanOut && aggregate != PlanStepOutputAggregates.Single)
            issues.Add("Steps without mapped inputs must use out.aggregate='single'.");

        if ((!string.IsNullOrWhiteSpace(step.Llm) || !string.IsNullOrWhiteSpace(step.Agent))
            && format == PlanStepOutputFormats.Json
            && explicitContract.Schema is null)
        {
            issues.Add("LLM and saved-agent steps with out.format='json' must provide out.schema.");
        }

        if (format == PlanStepOutputFormats.String && explicitContract.Schema is { } stringSchema)
        {
            if (!SchemaAcceptsType(stringSchema, JsonValueKind.String))
                issues.Add("out.schema must allow a string when out.format='string'.");
        }

        if (explicitContract.Schema is { } schema)
            issues.AddRange(ValidateSchemaDefinition(schema, "out.schema"));

        if (aggregate == PlanStepOutputAggregates.Flatten)
        {
            var schemaToCheck = explicitContract.Schema
                ?? toolMetadata?.OutputSchema;

            if (schemaToCheck is null || !SchemaDefinesArray(schemaToCheck.Value))
                issues.Add("out.aggregate='flatten' requires an array schema for each call.");
        }

        return issues;
    }

    public static bool SchemaDefinesArray(JsonElement schema) =>
        TryGetSchemaTypes(schema, out var types) && types.Contains("array", StringComparer.Ordinal);

    public static bool SchemaAcceptsType(JsonElement schema, JsonValueKind kind) =>
        !TryGetSchemaTypes(schema, out var types)
        || types.Contains(MapJsonKindToSchemaType(kind), StringComparer.Ordinal);

    public static bool TryGetSchemaType(JsonElement schema, out string? type)
    {
        type = null;
        if (!TryGetSchemaTypes(schema, out var types) || types.Count == 0)
            return false;

        type = types.FirstOrDefault(static candidate => !string.Equals(candidate, "null", StringComparison.Ordinal))
            ?? types[0];
        return true;
    }

    public static bool TryGetSchemaTypes(JsonElement schema, out IReadOnlyList<string> types)
    {
        types = Array.Empty<string>();
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        var collected = new List<string>();
        if (schema.TryGetProperty("type", out var typeElement))
        {
            switch (typeElement.ValueKind)
            {
                case JsonValueKind.String:
                    AddType(collected, typeElement.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var candidate in typeElement.EnumerateArray())
                    {
                        if (candidate.ValueKind == JsonValueKind.String)
                            AddType(collected, candidate.GetString());
                    }
                    break;
            }
        }

        if (schema.TryGetProperty("nullable", out var nullableElement)
            && nullableElement.ValueKind == JsonValueKind.True)
            AddType(collected, "null");

        if (collected.Count == 0)
        {
            if (schema.TryGetProperty("properties", out _)
                || schema.TryGetProperty("required", out _))
            {
                AddType(collected, "object");
            }

            if (schema.TryGetProperty("items", out _))
                AddType(collected, "array");
        }

        if (collected.Count == 0)
            return false;

        types = collected;
        return true;
    }

    public static IReadOnlyList<string> ValidateSchemaDefinition(JsonElement schema, string path)
    {
        var issues = new List<string>();

        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add($"{path} must be a JSON object.");
            return issues;
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            switch (typeElement.ValueKind)
            {
                case JsonValueKind.String:
                    if (string.IsNullOrWhiteSpace(typeElement.GetString()))
                        issues.Add($"{path}.type must be a non-empty string.");
                    break;
                case JsonValueKind.Array:
                    if (!typeElement.EnumerateArray().Any())
                    {
                        issues.Add($"{path}.type array must not be empty.");
                    }
                    else if (typeElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
                    {
                        issues.Add($"{path}.type array must contain only non-empty strings.");
                    }
                    break;
                default:
                    issues.Add($"{path}.type must be a string or array of strings.");
                    break;
            }
        }

        if (schema.TryGetProperty("nullable", out var nullableElement)
            && nullableElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            issues.Add($"{path}.nullable must be a boolean when present.");

        if (!TryGetSchemaTypes(schema, out var types)
            && !schema.TryGetProperty("enum", out _))
        {
            issues.Add($"{path} must declare 'type' or 'enum'.");
            return issues;
        }

        if (schema.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
            {
                issues.Add($"{path}.required must be an array of strings.");
            }
            else if (requiredElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            {
                issues.Add($"{path}.required must contain only non-empty strings.");
            }
        }

        if (schema.TryGetProperty("properties", out var propertiesElement))
        {
            if (propertiesElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add($"{path}.properties must be an object.");
            }
            else
            {
                foreach (var property in propertiesElement.EnumerateObject())
                    issues.AddRange(ValidateSchemaDefinition(property.Value, $"{path}.properties.{property.Name}"));
            }
        }

        if (schema.TryGetProperty("items", out var itemsElement))
            issues.AddRange(ValidateSchemaDefinition(itemsElement, $"{path}.items"));

        if (schema.TryGetProperty("enum", out var enumElement))
        {
            if (enumElement.ValueKind != JsonValueKind.Array || !enumElement.EnumerateArray().Any())
                issues.Add($"{path}.enum must be a non-empty array.");
        }

        if (types.Contains("array", StringComparer.Ordinal) && !schema.TryGetProperty("items", out _))
            issues.Add($"{path} with type='array' must define 'items'.");

        return issues;
    }

    private static string ResolveFormat(PlanStepOutputContract? explicitContract, AppToolDescriptor? toolMetadata)
    {
        if (explicitContract is not null && PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var explicitFormat))
            return explicitFormat;

        if (toolMetadata?.OutputSchema is { } toolOutputSchema
            && TryGetSchemaTypes(toolOutputSchema, out var schemaTypes)
            && schemaTypes.Contains("string", StringComparer.Ordinal)
            && schemaTypes.All(static type => type is "string" or "null"))
        {
            return PlanStepOutputFormats.String;
        }

        return PlanStepOutputFormats.Json;
    }

    private static string ResolveAggregate(PlanStepOutputContract? explicitContract, bool hasFanOut)
    {
        if (explicitContract is not null
            && PlanStepOutputAggregates.TryNormalize(explicitContract.Aggregate, out var explicitAggregate))
        {
            return explicitAggregate;
        }

        return hasFanOut
            ? PlanStepOutputAggregates.Collect
            : PlanStepOutputAggregates.Single;
    }

    private static JsonElement? ResolveCallSchema(
        PlanStepOutputContract? explicitContract,
        AppToolDescriptor? toolMetadata,
        string format)
    {
        if (explicitContract?.Schema is { } explicitSchema)
            return explicitSchema.Clone();

        if (format == PlanStepOutputFormats.String)
            return CreateStringSchema();

        return toolMetadata?.OutputSchema is { } toolOutputSchema
            ? toolOutputSchema.Clone()
            : null;
    }

    private static JsonElement? ResolveFinalSchema(JsonElement? callSchema, string aggregate)
    {
        if (callSchema is null)
            return null;

        return aggregate switch
        {
            PlanStepOutputAggregates.Single => callSchema.Value.Clone(),
            PlanStepOutputAggregates.Collect => CreateArraySchema(callSchema.Value),
            PlanStepOutputAggregates.Flatten => callSchema.Value.Clone(),
            _ => callSchema.Value.Clone()
        };
    }

    private static JsonElement CreateStringSchema() =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "string"
        });

    private static JsonElement CreateArraySchema(JsonElement itemSchema) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "array",
            ["items"] = JsonNode.Parse(itemSchema.GetRawText())
        });

    private static string MapJsonKindToSchemaType(JsonValueKind kind) =>
        kind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True => "boolean",
            JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "null",
            _ => "unknown"
        };

    private static void AddType(List<string> types, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var normalized = candidate.Trim().ToLowerInvariant();
        if (!types.Contains(normalized, StringComparer.Ordinal))
            types.Add(normalized);
    }
}
