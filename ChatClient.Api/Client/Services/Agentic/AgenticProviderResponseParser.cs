using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class AgenticProviderResponseParser
{
    public static ProviderAssistantResponse ParseOpenAiCompletion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty))
            {
                var message = ReadOpenAiError(errorProperty) ?? "OpenAI-compatible API returned an error.";
                return ProviderAssistantResponse.FromError(message);
            }

            if (!root.TryGetProperty("choices", out var choicesProperty) ||
                choicesProperty.ValueKind != JsonValueKind.Array ||
                choicesProperty.GetArrayLength() == 0)
            {
                return ProviderAssistantResponse.FromError("OpenAI-compatible response has no choices.");
            }

            var firstChoice = choicesProperty[0];
            if (!firstChoice.TryGetProperty("message", out var messageProperty) ||
                messageProperty.ValueKind != JsonValueKind.Object)
            {
                return ProviderAssistantResponse.FromError("OpenAI-compatible response does not contain a message.");
            }

            string content = messageProperty.TryGetProperty("content", out var contentProperty) &&
                             contentProperty.ValueKind == JsonValueKind.String
                ? contentProperty.GetString() ?? string.Empty
                : string.Empty;

            var toolCalls = ParseOpenAiToolCalls(messageProperty);
            return new ProviderAssistantResponse(content, toolCalls);
        }
        catch (JsonException ex)
        {
            return ProviderAssistantResponse.FromError($"Failed to parse OpenAI-compatible response: {ex.Message}");
        }
    }

    public static ProviderAssistantResponse ParseOllamaCompletion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String)
            {
                return ProviderAssistantResponse.FromError(errorProperty.GetString() ?? "Ollama API returned an error.");
            }

            if (!root.TryGetProperty("message", out var messageProperty) ||
                messageProperty.ValueKind != JsonValueKind.Object)
            {
                return ProviderAssistantResponse.FromError("Ollama response does not contain a message.");
            }

            string content = messageProperty.TryGetProperty("content", out var contentProperty) &&
                             contentProperty.ValueKind == JsonValueKind.String
                ? contentProperty.GetString() ?? string.Empty
                : string.Empty;

            var toolCalls = ParseOllamaToolCalls(messageProperty);
            return new ProviderAssistantResponse(content, toolCalls);
        }
        catch (JsonException ex)
        {
            return ProviderAssistantResponse.FromError($"Failed to parse Ollama response: {ex.Message}");
        }
    }

    public static bool TryReadOllamaChunk(string json, out string content, out bool done, out string? error)
    {
        content = string.Empty;
        done = false;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String)
            {
                error = errorProperty.GetString();
                return true;
            }

            if (root.TryGetProperty("message", out var messageProperty) &&
                messageProperty.ValueKind == JsonValueKind.Object &&
                messageProperty.TryGetProperty("content", out var contentProperty) &&
                contentProperty.ValueKind == JsonValueKind.String)
            {
                content = contentProperty.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("done", out var doneProperty) && doneProperty.ValueKind == JsonValueKind.True)
            {
                done = true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadOpenAiChunk(string json, out string content, out string? error)
    {
        content = string.Empty;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty) &&
                errorProperty.ValueKind == JsonValueKind.Object &&
                errorProperty.TryGetProperty("message", out var messageProperty) &&
                messageProperty.ValueKind == JsonValueKind.String)
            {
                error = messageProperty.GetString();
                return true;
            }

            if (!root.TryGetProperty("choices", out var choicesProperty) ||
                choicesProperty.ValueKind != JsonValueKind.Array ||
                choicesProperty.GetArrayLength() == 0)
            {
                return false;
            }

            var firstChoice = choicesProperty[0];
            if (firstChoice.TryGetProperty("delta", out var deltaProperty) &&
                deltaProperty.ValueKind == JsonValueKind.Object &&
                deltaProperty.TryGetProperty("content", out var contentProperty) &&
                contentProperty.ValueKind == JsonValueKind.String)
            {
                content = contentProperty.GetString() ?? string.Empty;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadOpenAiError(JsonElement errorProperty)
    {
        if (errorProperty.ValueKind == JsonValueKind.String)
        {
            return errorProperty.GetString();
        }

        if (errorProperty.ValueKind == JsonValueKind.Object &&
            errorProperty.TryGetProperty("message", out var messageProperty) &&
            messageProperty.ValueKind == JsonValueKind.String)
        {
            return messageProperty.GetString();
        }

        return null;
    }

    private static List<ProviderToolCall> ParseOpenAiToolCalls(JsonElement message)
    {
        List<ProviderToolCall> toolCalls = [];

        if (!message.TryGetProperty("tool_calls", out var toolCallsProperty) ||
            toolCallsProperty.ValueKind != JsonValueKind.Array)
        {
            return toolCalls;
        }

        int index = 0;
        foreach (var toolCallProperty in toolCallsProperty.EnumerateArray())
        {
            if (!toolCallProperty.TryGetProperty("function", out var functionProperty) ||
                functionProperty.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            string? name = functionProperty.TryGetProperty("name", out var nameProperty) &&
                           nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                index++;
                continue;
            }

            string id = toolCallProperty.TryGetProperty("id", out var idProperty) &&
                        idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString() ?? $"tool_call_{index}"
                : $"tool_call_{index}";

            string arguments = "{}";
            if (functionProperty.TryGetProperty("arguments", out var argumentsProperty))
            {
                arguments = argumentsProperty.ValueKind == JsonValueKind.String
                    ? NormalizeJson(argumentsProperty.GetString())
                    : NormalizeJson(argumentsProperty.GetRawText());
            }

            toolCalls.Add(new ProviderToolCall(id, name, arguments));
            index++;
        }

        return toolCalls;
    }

    private static List<ProviderToolCall> ParseOllamaToolCalls(JsonElement message)
    {
        List<ProviderToolCall> toolCalls = [];

        if (!message.TryGetProperty("tool_calls", out var toolCallsProperty) ||
            toolCallsProperty.ValueKind != JsonValueKind.Array)
        {
            return toolCalls;
        }

        int index = 0;
        foreach (var toolCallProperty in toolCallsProperty.EnumerateArray())
        {
            JsonElement functionProperty = toolCallProperty;
            if (toolCallProperty.TryGetProperty("function", out var nestedFunction) &&
                nestedFunction.ValueKind == JsonValueKind.Object)
            {
                functionProperty = nestedFunction;
            }

            string? name = functionProperty.TryGetProperty("name", out var nameProperty) &&
                           nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                index++;
                continue;
            }

            string id = toolCallProperty.TryGetProperty("id", out var idProperty) &&
                        idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString() ?? $"tool_call_{index}"
                : $"tool_call_{index}";

            string arguments = "{}";
            if (functionProperty.TryGetProperty("arguments", out var argumentsProperty))
            {
                arguments = argumentsProperty.ValueKind == JsonValueKind.String
                    ? NormalizeJson(argumentsProperty.GetString())
                    : NormalizeJson(argumentsProperty.GetRawText());
            }

            toolCalls.Add(new ProviderToolCall(id, name, arguments));
            index++;
        }

        return toolCalls;
    }

    private static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetRawText();
        }
        catch
        {
            return json;
        }
    }
}
