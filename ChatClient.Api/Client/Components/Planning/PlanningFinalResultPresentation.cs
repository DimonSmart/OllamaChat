using System.Text.Json;

namespace ChatClient.Api.Client.Components.Planning;

internal static class PlanningFinalResultPresentation
{
    private static readonly string[] UserFacingAnswerFieldNames =
    [
        "userFacingAnswer",
        "userFacingMarkdown",
        "markdown"
    ];

    private static readonly string[] SummaryFieldNames =
    [
        "userFacingAnswer",
        "summary",
        "markdown",
        "report",
        "answer",
        "result",
        "message",
        "text",
        "content"
    ];

    public static bool TryExtractUserFacingAnswer(JsonElement? data, out string markdown) =>
        TryExtractString(data, UserFacingAnswerFieldNames, out markdown);

    public static bool TryExtractSummary(JsonElement? data, out string summary)
    {
        if (TryExtractString(data, SummaryFieldNames, out summary))
        {
            return true;
        }

        if (data is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            summary = string.Empty;
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                summary = "Structured JSON result.";
                return true;
            case JsonValueKind.Array:
                summary = $"items: {value.GetArrayLength()}";
                return true;
            default:
                summary = value.GetRawText();
                return !string.IsNullOrWhiteSpace(summary);
        }
    }

    private static bool TryExtractString(JsonElement? data, IReadOnlyList<string> fieldNames, out string text)
    {
        text = string.Empty;
        if (data is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var fieldName in fieldNames)
        {
            if (!TryGetPropertyIgnoreCase(value, fieldName, out var property) || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            text = property.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
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
}
