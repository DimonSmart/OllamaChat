using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.PlanningRuntime.Common;

public static class PlanningLogFormatter
{
    private const int DefaultMaxDepth = 2;
    private const int DefaultMaxArrayItems = 3;
    private const int DefaultMaxObjectProperties = 8;
    private const int DefaultMaxStringLength = 160;

    public static string SummarizeText(string? value, int maxPreviewLength = DefaultMaxStringLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxPreviewLength)
            return normalized;

        return $"{normalized[..maxPreviewLength]}... (len={normalized.Length})";
    }

    public static string SummarizeElement(
        JsonElement? value,
        int maxDepth = DefaultMaxDepth,
        int maxArrayItems = DefaultMaxArrayItems,
        int maxObjectProperties = DefaultMaxObjectProperties,
        int maxStringLength = DefaultMaxStringLength) =>
        SummarizeNode(
            value is null ? null : JsonSerializer.SerializeToNode(value.Value),
            maxDepth,
            maxArrayItems,
            maxObjectProperties,
            maxStringLength);

    public static JsonNode? SummarizeElementValue(
        JsonElement? value,
        int maxDepth = DefaultMaxDepth,
        int maxArrayItems = DefaultMaxArrayItems,
        int maxObjectProperties = DefaultMaxObjectProperties,
        int maxStringLength = DefaultMaxStringLength) =>
        SummarizeNodeValue(
            value is null ? null : JsonSerializer.SerializeToNode(value.Value),
            maxDepth,
            maxArrayItems,
            maxObjectProperties,
            maxStringLength);

    public static string SummarizeNode(
        JsonNode? value,
        int maxDepth = DefaultMaxDepth,
        int maxArrayItems = DefaultMaxArrayItems,
        int maxObjectProperties = DefaultMaxObjectProperties,
        int maxStringLength = DefaultMaxStringLength) =>
        SummarizeNodeValue(
            value,
            maxDepth,
            maxArrayItems,
            maxObjectProperties,
            maxStringLength)
            ?.ToJsonString(PlanningJson.CompactOptions)
        ?? "null";

    public static JsonNode? SummarizeNodeValue(
        JsonNode? value,
        int maxDepth = DefaultMaxDepth,
        int maxArrayItems = DefaultMaxArrayItems,
        int maxObjectProperties = DefaultMaxObjectProperties,
        int maxStringLength = DefaultMaxStringLength) =>
        SummarizeNodeForDisplay(
            value,
            maxDepth,
            maxArrayItems,
            maxObjectProperties,
            maxStringLength);

    public static JsonObject SummarizePlan(PlanDefinition plan) => new()
    {
        ["goal"] = SummarizeText(plan.Goal, 120),
        ["stepCount"] = plan.Steps.Count,
        ["steps"] = new JsonArray(plan.Steps.Select(SummarizeStep).ToArray())
    };

    public static JsonObject SummarizeStep(PlanStep step) => new()
    {
        ["id"] = step.Id,
        ["kind"] = string.IsNullOrWhiteSpace(step.Tool) ? "llm" : "tool",
        ["name"] = step.Tool ?? step.Llm,
        ["inputKeys"] = new JsonArray(step.In.Keys.OrderBy(static key => key, StringComparer.Ordinal).Select(static key => JsonValue.Create(key)).ToArray()),
        ["output"] = step.Out is null
            ? null
            : new JsonObject
            {
                ["format"] = step.Out.Format,
                ["aggregate"] = step.Out.Aggregate
            },
        ["status"] = step.Status
    };

    public static JsonNode? SummarizeForLog(
        JsonNode? value,
        int maxDepth = 4,
        int maxArrayItems = 4,
        int maxObjectProperties = 12,
        int maxStringLength = DefaultMaxStringLength) =>
        SummarizeNodeForDisplay(value, maxDepth, maxArrayItems, maxObjectProperties, maxStringLength);

    private static JsonNode? SummarizeNodeForDisplay(
        JsonNode? value,
        int maxDepth,
        int maxArrayItems,
        int maxObjectProperties,
        int maxStringLength,
        int depth = 0)
    {
        if (value is null)
            return null;

        return value switch
        {
            JsonObject obj => SummarizeObject(obj, maxDepth, maxArrayItems, maxObjectProperties, maxStringLength, depth),
            JsonArray array => SummarizeArray(array, maxDepth, maxArrayItems, maxObjectProperties, maxStringLength, depth),
            JsonValue scalar => SummarizeValue(scalar, maxStringLength),
            _ => JsonValue.Create(value.ToJsonString(PlanningJson.CompactOptions))
        };
    }

    private static JsonNode SummarizeObject(
        JsonObject value,
        int maxDepth,
        int maxArrayItems,
        int maxObjectProperties,
        int maxStringLength,
        int depth)
    {
        var properties = value.ToList();
        if (depth >= maxDepth)
        {
            return new JsonObject
            {
                ["kind"] = "object",
                ["propertyCount"] = properties.Count,
                ["keys"] = new JsonArray(properties.Take(maxObjectProperties).Select(static property => JsonValue.Create(property.Key)).ToArray())
            };
        }

        var summary = new JsonObject();
        foreach (var property in properties.Take(maxObjectProperties))
        {
            summary[property.Key] = SummarizeNodeForDisplay(
                property.Value,
                maxDepth,
                maxArrayItems,
                maxObjectProperties,
                maxStringLength,
                depth + 1);
        }

        if (properties.Count > maxObjectProperties)
            summary["__truncatedProperties"] = properties.Count - maxObjectProperties;

        return summary;
    }

    private static JsonNode SummarizeArray(
        JsonArray value,
        int maxDepth,
        int maxArrayItems,
        int maxObjectProperties,
        int maxStringLength,
        int depth)
    {
        var summary = new JsonObject
        {
            ["kind"] = "array",
            ["count"] = value.Count
        };

        if (value.Count == 0)
            return summary;

        summary["sample"] = new JsonArray(value.Take(maxArrayItems)
            .Select(item => SummarizeNodeForDisplay(
                item,
                maxDepth,
                maxArrayItems,
                maxObjectProperties,
                maxStringLength,
                depth + 1))
            .ToArray());

        if (value.Count > maxArrayItems)
            summary["truncatedCount"] = value.Count - maxArrayItems;

        return summary;
    }

    private static JsonNode SummarizeValue(JsonValue value, int maxStringLength)
    {
        if (value.TryGetValue<string>(out var text))
        {
            if (text.Length <= maxStringLength)
                return JsonValue.Create(text);

            return new JsonObject
            {
                ["kind"] = "string",
                ["length"] = text.Length,
                ["preview"] = SummarizeText(text, maxStringLength)
            };
        }

        return value.DeepClone();
    }
}
