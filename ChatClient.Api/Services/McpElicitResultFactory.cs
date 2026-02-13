using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services;

internal static class McpElicitResultFactory
{
    public static ElicitResult Create(McpElicitationResponse response)
    {
        string action = NormalizeAction(response.Action);
        var payload = new Dictionary<string, object?>
        {
            ["action"] = action
        };

        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedContent = NormalizeContent(response.Content);
            if (normalizedContent is { Count: > 0 })
            {
                payload["content"] = normalizedContent;
            }
        }

        var result = JsonSerializer.Deserialize<ElicitResult>(JsonSerializer.Serialize(payload));
        if (result is not null)
        {
            return result;
        }

        return JsonSerializer.Deserialize<ElicitResult>("{\"action\":\"cancel\"}")!;
    }

    private static string NormalizeAction(string? action)
    {
        return action?.ToLowerInvariant() switch
        {
            "accept" => "accept",
            "decline" => "decline",
            _ => "cancel"
        };
    }

    private static IReadOnlyDictionary<string, object?>? NormalizeContent(IReadOnlyDictionary<string, object?>? content)
    {
        if (content is null || content.Count == 0)
            return null;

        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in content)
        {
            normalized[key] = NormalizeContentValue(value);
        }

        return normalized;
    }

    private static object? NormalizeContentValue(object? value)
    {
        if (value is null)
            return null;

        if (value is JsonElement element)
            return NormalizeJsonElement(element);

        if (value is bool or string or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;

        if (value is IEnumerable<string> stringEnumerable && value is not string)
            return stringEnumerable.ToArray();

        if (value is IEnumerable<object?> objectEnumerable && value is not string)
            return objectEnumerable.Select(NormalizeContentValue).ToArray();

        return value.ToString();
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var whole)
                ? whole
                : element.TryGetDouble(out var fractional) ? fractional : element.GetRawText(),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }
}
