using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class ToolArgumentSchemaValidator
{
    public static bool TryValidateAndParse(
        string arguments,
        JsonElement schema,
        out Dictionary<string, object?> parsedArguments,
        out string normalizedRequest,
        out string error)
    {
        parsedArguments = new Dictionary<string, object?>();
        normalizedRequest = "{}";
        error = string.Empty;

        string payload = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            normalizedRequest = payload;
            error = $"Tool arguments are not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            normalizedRequest = root.GetRawText();

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Tool arguments must be a JSON object.";
                return false;
            }

            if (!ValidateAgainstSchema(root, schema, out error))
            {
                return false;
            }

            parsedArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(normalizedRequest) ??
                              new Dictionary<string, object?>();
            return true;
        }
    }

    private static bool ValidateAgainstSchema(JsonElement arguments, JsonElement schema, out string error)
    {
        error = string.Empty;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (schema.TryGetProperty("required", out var requiredProperty) &&
            requiredProperty.ValueKind == JsonValueKind.Array)
        {
            var argumentNames = arguments
                .EnumerateObject()
                .Select(static p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var requiredItem in requiredProperty.EnumerateArray())
            {
                if (requiredItem.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string requiredName = requiredItem.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(requiredName) && !argumentNames.Contains(requiredName))
                {
                    error = $"Missing required argument '{requiredName}'.";
                    return false;
                }
            }
        }

        if (schema.TryGetProperty("properties", out var propertiesSchema) &&
            propertiesSchema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
            additionalProperties.ValueKind == JsonValueKind.False)
        {
            var allowed = propertiesSchema
                .EnumerateObject()
                .Select(static p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var argument in arguments.EnumerateObject())
            {
                if (!allowed.Contains(argument.Name))
                {
                    error = $"Argument '{argument.Name}' is not allowed by tool schema.";
                    return false;
                }
            }
        }

        if (schema.TryGetProperty("properties", out propertiesSchema) &&
            propertiesSchema.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertySchema in propertiesSchema.EnumerateObject())
            {
                if (!arguments.TryGetProperty(propertySchema.Name, out var argumentValue))
                {
                    continue;
                }

                if (!IsJsonTypeCompatible(argumentValue, propertySchema.Value))
                {
                    error = $"Argument '{propertySchema.Name}' does not match schema type.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsJsonTypeCompatible(JsonElement value, JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out var typeProperty))
        {
            return true;
        }

        return typeProperty.ValueKind switch
        {
            JsonValueKind.String => IsTypeMatch(value, typeProperty.GetString()),
            JsonValueKind.Array => typeProperty.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Any(item => IsTypeMatch(value, item.GetString())),
            _ => true
        };
    }

    private static bool IsTypeMatch(JsonElement value, string? schemaType)
    {
        return schemaType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }
}
