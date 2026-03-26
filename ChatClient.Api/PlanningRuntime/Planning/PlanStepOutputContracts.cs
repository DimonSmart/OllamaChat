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

public sealed class PlanStepOutputContract
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = PlanStepOutputFormats.Json;

    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }
}

public enum DerivedStepOutputContractSource
{
    ToolOutputSchema,
    ExplicitOutputSchema,
    DerivedFromToolConsumer,
    DerivedFromBindingPath,
    Opaque
}

public sealed record ResolvedPlanStepOutputContract(
    string Format,
    bool IsMapped,
    JsonElement? CallSchema,
    JsonElement? FinalSchema,
    DerivedStepOutputContractSource Source,
    bool IsOpaque)
{
    public JsonElement? ItemSchema =>
        PlanStepOutputContractResolver.TryGetItemSchema(this, out var itemSchema)
            ? itemSchema
            : null;
}

public static class PlanStepOutputContractResolver
{
    public static bool HasMappedInputs(PlanStep step) =>
        step.In.Values.Any(value =>
            PlanInputBindingSyntax.TryParseBinding(value, out var bindingExpression, out var bindingError)
            && string.IsNullOrWhiteSpace(bindingError)
            && bindingExpression is not null
            && PlanInputBindingSyntax.EnumerateBindings(bindingExpression)
                .Any(binding => binding.Mode == PlanInputBindingMode.Map));

    public static IReadOnlyList<string> ValidateContractDefinition(
        PlanStep step,
        AppToolDescriptor? toolMetadata = null)
    {
        var issues = new List<string>();
        var explicitContract = step.Out;
        var isTool = PlanStepKinds.IsTool(step);
        var isLlm = PlanStepKinds.IsLlm(step);
        var isAgent = PlanStepKinds.IsAgent(step);

        if (isTool)
        {
            if (explicitContract is not null)
                issues.Add("Tool steps must not declare 'out'.");

            return issues;
        }

        if ((isLlm || isAgent) && explicitContract is null)
        {
            issues.Add("LLM and saved-agent steps must declare an 'out' contract.");
            return issues;
        }

        if (explicitContract is null)
            return issues;

        if (!PlanStepOutputFormats.TryNormalize(explicitContract.Format, out var format))
            issues.Add("out.format must be 'json' or 'string'.");

        if (issues.Count > 0)
            return issues;

        if (format == PlanStepOutputFormats.String && explicitContract.Schema is { } stringSchema)
        {
            if (!SchemaAcceptsType(stringSchema, JsonValueKind.String))
                issues.Add("out.schema must allow a string when out.format='string'.");
        }

        if (explicitContract.Schema is { } schema)
            issues.AddRange(ValidateSchemaDefinition(schema, "out.schema"));

        return issues;
    }

    public static bool SchemaDefinesArray(JsonElement schema) =>
        TryGetSchemaTypes(schema, out var types) && types.Contains("array", StringComparer.Ordinal);

    public static bool SchemaAcceptsType(JsonElement schema, JsonValueKind kind) =>
        !TryGetSchemaTypes(schema, out var types)
        || types.Contains(MapJsonKindToSchemaType(kind), StringComparer.Ordinal);

    public static bool TryGetItemSchema(
        ResolvedPlanStepOutputContract contract,
        out JsonElement itemSchema)
    {
        itemSchema = default;
        if (!contract.IsMapped)
            return false;

        if (contract.FinalSchema is { } finalSchema
            && TryGetArrayItemSchema(finalSchema, out itemSchema))
        {
            return true;
        }

        if (contract.CallSchema is { } callSchema)
        {
            if (TryGetArrayItemSchema(callSchema, out itemSchema))
                return true;

            itemSchema = callSchema.Clone();
            return true;
        }

        return false;
    }

    public static bool TryGetArrayItemSchema(JsonElement schema, out JsonElement itemSchema)
    {
        itemSchema = default;
        if (!SchemaDefinesArray(schema) || !schema.TryGetProperty("items", out var itemsSchema))
            return false;

        itemSchema = itemsSchema.Clone();
        return true;
    }

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
        {
            AddType(collected, "null");
        }

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
        {
            issues.Add($"{path}.nullable must be a boolean when present.");
        }

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

    public static JsonElement CreateStringSchema() =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "string"
        });

    public static JsonElement CreateOpaqueSchema() =>
        JsonSerializer.SerializeToElement(new JsonObject());

    public static JsonElement CreateArraySchema(JsonElement itemSchema) =>
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
