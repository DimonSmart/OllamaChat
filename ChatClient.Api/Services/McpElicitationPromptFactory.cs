using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services;

internal static class McpElicitationPromptFactory
{
    public static McpElicitationPrompt Build(string serverName, ElicitRequestParams request)
    {
        var requestJson = JsonSerializer.SerializeToElement(request);

        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? ReadString(requestJson, "mode") ?? "form"
            : request.Mode;
        var message = string.IsNullOrWhiteSpace(request.Message)
            ? ReadString(requestJson, "message") ?? "MCP server requested additional user input."
            : request.Message;
        var url = string.IsNullOrWhiteSpace(request.Url)
            ? ReadString(requestJson, "url")
            : request.Url;

        var elicitationId = ReadString(requestJson, "elicitationId");
        var fields = string.Equals(mode, "form", StringComparison.OrdinalIgnoreCase)
            ? ParseFields(requestJson)
            : [];

        return new McpElicitationPrompt(serverName, mode, message, url, elicitationId, fields);
    }

    private static IReadOnlyList<McpElicitationField> ParseFields(JsonElement requestJson)
    {
        if (!TryGetPropertyIgnoreCase(requestJson, "requestedSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var requiredFields = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetPropertyIgnoreCase(schema, "required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var fieldName = item.GetString();
                    if (!string.IsNullOrWhiteSpace(fieldName))
                    {
                        requiredFields.Add(fieldName);
                    }
                }
            }
        }

        if (TryGetPropertyIgnoreCase(schema, "properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            var fields = new List<McpElicitationField>();
            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(ParseField(
                    property.Name,
                    property.Value,
                    requiredFields.Contains(property.Name)));
            }

            return fields;
        }

        if (TryGetPropertyIgnoreCase(schema, "type", out _) ||
            TryGetPropertyIgnoreCase(schema, "enum", out _) ||
            TryGetPropertyIgnoreCase(schema, "oneOf", out _) ||
            TryGetPropertyIgnoreCase(schema, "anyOf", out _))
        {
            return [ParseField("value", schema, requiredFields.Contains("value"))];
        }

        return [];
    }

    private static McpElicitationField ParseField(string name, JsonElement schema, bool required)
    {
        var options = ParseOptions(schema, out var isMultiSelect);

        var type = ReadString(schema, "type")?.ToLowerInvariant();
        if (isMultiSelect)
        {
            type = "array";
        }
        else if (string.IsNullOrWhiteSpace(type))
        {
            type = "string";
        }

        JsonElement? defaultValue = null;
        if (TryGetPropertyIgnoreCase(schema, "default", out var schemaDefault))
        {
            defaultValue = schemaDefault.Clone();
        }

        var label = ReadString(schema, "title");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = name;
        }

        return new McpElicitationField(
            Name: name,
            Label: label,
            Type: type ?? "string",
            Description: ReadString(schema, "description"),
            Required: required,
            IsMultiSelect: isMultiSelect,
            Options: options,
            DefaultValue: defaultValue,
            MinItems: ReadInt(schema, "minItems"),
            MaxItems: ReadInt(schema, "maxItems"));
    }

    private static IReadOnlyList<McpElicitationOption> ParseOptions(JsonElement schema, out bool isMultiSelect)
    {
        var options = new List<McpElicitationOption>();
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);

        isMultiSelect = string.Equals(ReadString(schema, "type"), "array", StringComparison.OrdinalIgnoreCase);
        if (isMultiSelect && TryGetPropertyIgnoreCase(schema, "items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            AddOptionsFromConstArrays(items, "oneOf", options, uniqueValues);
            AddOptionsFromConstArrays(items, "anyOf", options, uniqueValues);
            AddOptionsFromEnum(items, options, uniqueValues);
            return options;
        }

        AddOptionsFromConstArrays(schema, "oneOf", options, uniqueValues);
        AddOptionsFromConstArrays(schema, "anyOf", options, uniqueValues);
        AddOptionsFromEnum(schema, options, uniqueValues);

        return options;
    }

    private static void AddOptionsFromConstArrays(
        JsonElement source,
        string propertyName,
        List<McpElicitationOption> target,
        HashSet<string> uniqueValues)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var optionsArray) ||
            optionsArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var option in optionsArray.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetPropertyIgnoreCase(option, "const", out var constValue))
                continue;

            var value = JsonElementToString(constValue);
            if (string.IsNullOrWhiteSpace(value) || !uniqueValues.Add(value))
                continue;

            var label = ReadString(option, "title");
            target.Add(new McpElicitationOption(value, string.IsNullOrWhiteSpace(label) ? value : label));
        }
    }

    private static void AddOptionsFromEnum(
        JsonElement source,
        List<McpElicitationOption> target,
        HashSet<string> uniqueValues)
    {
        if (!TryGetPropertyIgnoreCase(source, "enum", out var enumArray) ||
            enumArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<string>? enumNames = null;
        if (TryGetPropertyIgnoreCase(source, "enumNames", out var enumNamesArray) &&
            enumNamesArray.ValueKind == JsonValueKind.Array)
        {
            enumNames = enumNamesArray.EnumerateArray()
                .Select(JsonElementToString)
                .ToList();
        }

        var index = 0;
        foreach (var enumValue in enumArray.EnumerateArray())
        {
            var value = JsonElementToString(enumValue);
            if (string.IsNullOrWhiteSpace(value) || !uniqueValues.Add(value))
            {
                index++;
                continue;
            }

            string label = value;
            if (enumNames is { Count: > 0 } && index < enumNames.Count && !string.IsNullOrWhiteSpace(enumNames[index]))
            {
                label = enumNames[index];
            }

            target.Add(new McpElicitationOption(value, label));
            index++;
        }
    }

    private static int? ReadInt(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;

        return null;
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
    }
}
