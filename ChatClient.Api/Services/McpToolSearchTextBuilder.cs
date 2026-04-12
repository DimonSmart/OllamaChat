using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services;

internal static class McpToolSearchTextBuilder
{
    public static string Build(
        string serverName,
        string toolName,
        string? toolDescription,
        JsonElement inputSchema,
        JsonElement? outputSchema)
    {
        StringBuilder builder = new();

        AppendSentence(builder, serverName);
        AppendSentence(builder, toolName);
        AppendSentence(builder, toolDescription);

        var inputText = BuildSchemaText(inputSchema, "Inputs");
        if (!string.IsNullOrWhiteSpace(inputText))
        {
            AppendSentence(builder, inputText);
        }

        if (outputSchema is JsonElement outputValue)
        {
            var outputText = BuildSchemaText(outputValue, "Outputs");
            if (!string.IsNullOrWhiteSpace(outputText))
            {
                AppendSentence(builder, outputText);
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildSchemaText(JsonElement schema, string prefix)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        builder.Append(prefix);
        builder.Append(": ");
        AppendSchemaDetails(builder, schema, depth: 0);
        return builder.ToString().Trim();
    }

    private static void AppendSchemaDetails(StringBuilder builder, JsonElement schema, int depth)
    {
        if (depth > 2 || schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("description", out var descriptionElement) &&
            descriptionElement.ValueKind == JsonValueKind.String)
        {
            AppendFragment(builder, descriptionElement.GetString());
        }

        if (schema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            var requiredValues = requiredElement
                .EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (requiredValues.Length > 0)
            {
                AppendFragment(builder, $"Required: {string.Join(", ", requiredValues!)}");
            }
        }

        if (schema.TryGetProperty("enum", out var enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array)
        {
            var enumValues = enumElement
                .EnumerateArray()
                .Select(ReadScalarValue)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (enumValues.Length > 0)
            {
                AppendFragment(builder, $"Allowed values: {string.Join(", ", enumValues!)}");
            }
        }

        if (schema.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                AppendFragment(builder, $"Field {property.Name}");
                AppendSchemaDetails(builder, property.Value, depth + 1);
            }
        }

        if (schema.TryGetProperty("items", out var itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Object)
        {
            AppendFragment(builder, "Array items");
            AppendSchemaDetails(builder, itemsElement, depth + 1);
        }
    }

    private static string? ReadScalarValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

    private static void AppendSentence(StringBuilder builder, string? text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(normalized);
        if (!normalized.EndsWith('.'))
        {
            builder.Append('.');
        }
    }

    private static void AppendFragment(StringBuilder builder, string? text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return;
        }

        if (builder.Length > 0 && builder[^1] is not ' ' and not ':')
        {
            builder.Append("; ");
        }

        builder.Append(normalized);
    }

    private static string NormalizeText(string? text) =>
        text?.ReplaceLineEndings(" ").Trim() ?? string.Empty;
}
