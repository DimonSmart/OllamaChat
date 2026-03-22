using ChatClient.Domain.Models;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class AgenticToolUtility
{
    public static JsonElement ParseToolSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return document.RootElement.Clone();
    }

    public static string ReadRequiredStringArgument(Dictionary<string, object?> arguments, string argumentName)
    {
        var value = ReadOptionalStringArgument(arguments, argumentName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Argument '{argumentName}' is required.");
        }

        return value;
    }

    public static string? ReadOptionalStringArgument(Dictionary<string, object?> arguments, string argumentName)
    {
        if (!arguments.TryGetValue(argumentName, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is string text)
        {
            return text;
        }

        if (raw is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Null => null,
                _ => json.GetRawText()
            };
        }

        return raw.ToString();
    }

    public static string BuildWhiteboardSnapshot(WhiteboardState whiteboard)
    {
        if (whiteboard.Notes.Count == 0)
        {
            return "Whiteboard is empty.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Current whiteboard notes:");
        for (int i = 0; i < whiteboard.Notes.Count; i++)
        {
            var note = whiteboard.Notes[i];
            builder.Append("- ");
            builder.Append(i + 1);
            builder.Append(". ");

            if (!string.IsNullOrWhiteSpace(note.Author))
            {
                builder.Append('[');
                builder.Append(note.Author);
                builder.Append("] ");
            }

            builder.Append(note.Content);
            builder.Append(" (created at ");
            builder.Append(note.CreatedAt.ToLocalTime().ToString("u"));
            builder.AppendLine(")");
        }

        return builder.ToString().Trim();
    }

    public static string SerializeForToolTransport(object? value, JsonSerializerOptions jsonOptions)
    {
        try
        {
            return JsonSerializer.Serialize(value, jsonOptions);
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }

}
