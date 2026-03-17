using System.Text.Json;

namespace ChatClient.Api.Client.Pages;

public static class McpPlaygroundToolFormHelper
{
    public static IReadOnlyList<FieldDefinition> CreateFields(JsonElement toolSchema)
    {
        if (toolSchema.ValueKind != JsonValueKind.Object ||
            !toolSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var requiredFields = GetRequiredFields(toolSchema);
        List<FieldDefinition> fields = [];

        foreach (var property in properties.EnumerateObject())
        {
            var description = property.Value.TryGetProperty("description", out var descriptionNode) &&
                              descriptionNode.ValueKind == JsonValueKind.String
                ? descriptionNode.GetString()
                : null;

            fields.Add(new FieldDefinition(
                property.Name,
                ResolveType(property.Value),
                description,
                requiredFields.Contains(property.Name)));
        }

        return fields;
    }

    public static Dictionary<string, object?> BuildArguments(
        IEnumerable<FieldDefinition> fields,
        IReadOnlyDictionary<string, object?> parameters)
    {
        Dictionary<string, object?> args = new(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            if (!parameters.TryGetValue(field.Name, out var value))
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            if (value is string textValue)
            {
                if ((field.Type == "array" || field.Type == "object"))
                {
                    if (string.IsNullOrWhiteSpace(textValue))
                    {
                        continue;
                    }

                    try
                    {
                        value = JsonSerializer.Deserialize<JsonElement>(textValue);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidPlaygroundInputException(
                            field.Name,
                            CreateJsonInputErrorMessage(field),
                            ex);
                    }
                }
                else if (!field.IsRequired && string.IsNullOrWhiteSpace(textValue))
                {
                    continue;
                }
            }

            args[field.Name] = value;
        }

        return args;
    }

    public static string ResolveType(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var typeNode))
        {
            var resolvedType = ResolveTypeNode(typeNode);
            if (!string.IsNullOrWhiteSpace(resolvedType))
            {
                return resolvedType;
            }
        }

        if (TryResolveCompositeType(schema, "anyOf", out var anyOfType) ||
            TryResolveCompositeType(schema, "oneOf", out anyOfType))
        {
            return anyOfType;
        }

        if (schema.TryGetProperty("items", out _))
        {
            return "array";
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return "object";
        }

        return "string";
    }

    private static HashSet<string> GetRequiredFields(JsonElement schema)
    {
        HashSet<string> requiredFields = new(StringComparer.OrdinalIgnoreCase);

        if (!schema.TryGetProperty("required", out var requiredNode) ||
            requiredNode.ValueKind != JsonValueKind.Array)
        {
            return requiredFields;
        }

        foreach (var requiredField in requiredNode.EnumerateArray())
        {
            if (requiredField.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(requiredField.GetString()))
            {
                requiredFields.Add(requiredField.GetString()!);
            }
        }

        return requiredFields;
    }

    private static bool TryResolveCompositeType(
        JsonElement schema,
        string propertyName,
        out string resolvedType)
    {
        resolvedType = string.Empty;

        if (!schema.TryGetProperty(propertyName, out var variants) ||
            variants.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var variant in variants.EnumerateArray())
        {
            var variantType = ResolveType(variant);
            if (!string.IsNullOrWhiteSpace(variantType) &&
                !string.Equals(variantType, "null", StringComparison.OrdinalIgnoreCase))
            {
                resolvedType = variantType;
                return true;
            }
        }

        return false;
    }

    private static string? ResolveTypeNode(JsonElement typeNode)
    {
        return typeNode.ValueKind switch
        {
            JsonValueKind.String => typeNode.GetString(),
            JsonValueKind.Array => typeNode
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item) &&
                                               !string.Equals(item, "null", StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
    }

    private static string CreateJsonInputErrorMessage(FieldDefinition field)
    {
        return field.Type switch
        {
            "array" => $"Field '{field.Name}' expects a JSON array. Example: [\"Readme\"]",
            "object" => $"Field '{field.Name}' expects a JSON object. Example: {{\"key\":\"value\"}}",
            _ => $"Field '{field.Name}' expects valid JSON."
        };
    }

    public sealed record FieldDefinition(string Name, string Type, string? Description, bool IsRequired);

    public sealed class InvalidPlaygroundInputException(string fieldName, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public string FieldName { get; } = fieldName;
    }
}
