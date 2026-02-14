using ChatClient.Domain.Models;
using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class AgenticProviderPayloadBuilder
{
    public static string BuildOllamaPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        bool stream,
        IReadOnlyList<ToolBinding>? tools,
        JsonSerializerOptions jsonOptions,
        JsonElement emptyToolSchema)
    {
        var options = new Dictionary<string, object>();
        if (agent.Temperature.HasValue)
        {
            options["temperature"] = agent.Temperature.Value;
        }

        if (agent.RepeatPenalty.HasValue)
        {
            options["repeat_penalty"] = agent.RepeatPenalty.Value;
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = ToOllamaPayloadMessages(messages),
            ["stream"] = stream,
            ["options"] = options.Count == 0 ? null : options
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = BuildProviderToolDefinitions(tools, emptyToolSchema);
        }

        return JsonSerializer.Serialize(payload, jsonOptions);
    }

    public static string BuildOpenAiPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        bool stream,
        IReadOnlyList<ToolBinding>? tools,
        JsonSerializerOptions jsonOptions,
        JsonElement emptyToolSchema)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = ToOpenAiPayloadMessages(messages),
            ["stream"] = stream
        };

        if (agent.Temperature.HasValue)
        {
            payload["temperature"] = agent.Temperature.Value;
        }

        if (tools is { Count: > 0 })
        {
            payload["tools"] = BuildProviderToolDefinitions(tools, emptyToolSchema);
        }

        return JsonSerializer.Serialize(payload, jsonOptions);
    }

    private static List<Dictionary<string, object?>> ToOpenAiPayloadMessages(IReadOnlyList<ProviderMessage> messages)
    {
        List<Dictionary<string, object?>> payloadMessages = [];

        foreach (var message in messages)
        {
            Dictionary<string, object?> payload = new()
            {
                ["role"] = message.Role
            };

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                payload["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content;
                payload["tool_calls"] = message.ToolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                }).ToList();
            }
            else
            {
                payload["content"] = message.Content ?? string.Empty;
            }

            if (message.Role == "tool")
            {
                if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    payload["tool_call_id"] = message.ToolCallId;
                }

                if (!string.IsNullOrWhiteSpace(message.Name))
                {
                    payload["name"] = message.Name;
                }
            }

            payloadMessages.Add(payload);
        }

        return payloadMessages;
    }

    private static List<Dictionary<string, object?>> ToOllamaPayloadMessages(IReadOnlyList<ProviderMessage> messages)
    {
        List<Dictionary<string, object?>> payloadMessages = [];

        foreach (var message in messages)
        {
            Dictionary<string, object?> payload = new()
            {
                ["role"] = message.Role,
                ["content"] = message.Content ?? string.Empty
            };

            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            {
                payload["tool_calls"] = message.ToolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = ParseArgumentsForOllama(tc.Arguments)
                    }
                }).ToList();
            }

            if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.Name))
            {
                payload["tool_name"] = message.Name;
            }

            payloadMessages.Add(payload);
        }

        return payloadMessages;
    }

    private static List<Dictionary<string, object?>> BuildProviderToolDefinitions(
        IReadOnlyList<ToolBinding> tools,
        JsonElement emptyToolSchema)
    {
        List<Dictionary<string, object?>> result = [];

        foreach (var tool in tools)
        {
            JsonElement schema = tool.JsonSchema.ValueKind == JsonValueKind.Undefined
                ? emptyToolSchema
                : tool.JsonSchema.Clone();

            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.ProviderName,
                    ["description"] = tool.Description,
                    ["parameters"] = schema
                }
            });
        }

        return result;
    }

    private static object ParseArgumentsForOllama(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch
        {
            return arguments;
        }
    }
}
