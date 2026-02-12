using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HttpAgenticExecutionRuntime(
    ILlmServerConfigService llmServerConfigService,
    IUserSettingsService userSettingsService,
    IModelCapabilityService modelCapabilityService,
    IMcpClientService mcpClientService,
    KernelService kernelService,
    IConfiguration configuration,
    ILogger<HttpAgenticExecutionRuntime> logger) : IAgenticExecutionRuntime
{
    private const int MaxToolRounds = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonElement EmptyToolSchema = CreateEmptyToolSchema();

    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgenticExecutionRuntimeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string modelName = request.Agent.ModelName
            ?? request.Configuration.ModelName
            ?? throw new InvalidOperationException($"Agent '{request.Agent.AgentName}' model name is not set.");

        LlmServerConfig? server = await LlmServerConfigHelper.GetServerConfigAsync(
            llmServerConfigService,
            userSettingsService,
            request.Agent.LlmId);

        if (server is null)
        {
            yield return new ChatEngineStreamChunk(
                request.Agent.AgentName,
                "No configured LLM server is available for the selected model.",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        Guid resolvedServerId = request.Agent.LlmId is { } agentServerId && agentServerId != Guid.Empty
            ? agentServerId
            : server.Id ?? Guid.Empty;

        bool supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
            new ServerModel(resolvedServerId, modelName),
            cancellationToken);

        var requestedFunctions = await ResolveRequestedFunctionNamesAsync(request, cancellationToken);
        if (!supportsFunctions && requestedFunctions.Count > 0)
        {
            logger.LogInformation(
                "Model {ModelName} for agent {AgentName} does not support function calling. Skipping {FunctionCount} configured tools.",
                modelName,
                request.Agent.AgentName,
                requestedFunctions.Count);
        }

        if (request.Configuration.UseWhiteboard)
        {
            logger.LogInformation(
                "HTTP agentic runtime does not yet expose the whiteboard plugin as a tool. Whiteboard setting: {WhiteboardEnabled}",
                request.Configuration.UseWhiteboard);
        }

        var messages = BuildProviderMessages(request);
        var toolRegistry = supportsFunctions
            ? await ResolveToolRegistryAsync(requestedFunctions, cancellationToken)
            : ToolRegistry.Empty;

        if (requestedFunctions.Count > 0 && !toolRegistry.HasTools)
        {
            logger.LogWarning(
                "No MCP tools matched the configured function set for agent {AgentName}. Requested: [{RequestedFunctions}]",
                request.Agent.AgentName,
                string.Join(", ", requestedFunctions));
        }

        if (toolRegistry.HasTools)
        {
            await foreach (var chunk in StreamWithToolsAsync(
                               server,
                               request.Agent,
                               modelName,
                               messages,
                               toolRegistry,
                               cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

        if (server.ServerType == ServerType.ChatGpt)
        {
            await foreach (var chunk in StreamOpenAiCompatibleAsync(
                               server,
                               request.Agent,
                               modelName,
                               messages,
                               cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

        await foreach (var chunk in StreamOllamaAsync(
                           server,
                           request.Agent,
                           modelName,
                           messages,
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<ChatEngineStreamChunk> StreamWithToolsAsync(
        LlmServerConfig server,
        AgentDescription agent,
        string modelName,
        List<ProviderMessage> messages,
        ToolRegistry toolRegistry,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? apiKey = null;
        string endpoint;
        HttpClient client;

        if (server.ServerType == ServerType.ChatGpt)
        {
            apiKey = ResolveOpenAiApiKey(server);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                yield return new ChatEngineStreamChunk(
                    agent.AgentName,
                    "OpenAI API key is required but not configured.",
                    IsFinal: true,
                    IsError: true);
                yield break;
            }

            client = CreateHttpClient(server, "https://api.openai.com");
            endpoint = BuildOpenAiChatEndpoint(server);
        }
        else
        {
            client = CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);
            endpoint = BuildOllamaChatEndpoint(server);
        }

        using (client)
        {
            List<FunctionCallRecord> functionCalls = [];

            for (int round = 0; round < MaxToolRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ProviderAssistantResponse completion = server.ServerType == ServerType.ChatGpt
                    ? await CompleteOpenAiWithToolsAsync(
                        client,
                        endpoint,
                        apiKey!,
                        modelName,
                        agent,
                        messages,
                        toolRegistry,
                        cancellationToken)
                    : await CompleteOllamaWithToolsAsync(
                        client,
                        endpoint,
                        modelName,
                        agent,
                        messages,
                        toolRegistry,
                        cancellationToken);

                if (completion.HasError)
                {
                    yield return new ChatEngineStreamChunk(
                        agent.AgentName,
                        completion.Error ?? "Tool-enabled completion failed.",
                        IsFinal: true,
                        IsError: true);
                    yield break;
                }

                messages.Add(ToAssistantMessage(completion));

                if (completion.ToolCalls.Count == 0)
                {
                    if (!string.IsNullOrEmpty(completion.Content))
                    {
                        yield return new ChatEngineStreamChunk(agent.AgentName, completion.Content);
                    }

                    yield return new ChatEngineStreamChunk(
                        agent.AgentName,
                        string.Empty,
                        IsFinal: true,
                        FunctionCalls: functionCalls.Count > 0 ? functionCalls : null);
                    yield break;
                }

                foreach (var toolCall in completion.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var execution = await ExecuteToolCallAsync(toolCall, toolRegistry, cancellationToken);
                    functionCalls.Add(execution.Record);
                    messages.Add(execution.ToolMessage);
                }
            }
        }

        yield return new ChatEngineStreamChunk(
            agent.AgentName,
            $"Tool calling exceeded safety limit ({MaxToolRounds} rounds).",
            IsFinal: true,
            IsError: true);
    }

    private async Task<ProviderAssistantResponse> CompleteOpenAiWithToolsAsync(
        HttpClient client,
        string endpoint,
        string apiKey,
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        ToolRegistry toolRegistry,
        CancellationToken cancellationToken)
    {
        string payload = BuildOpenAiPayload(
            modelName,
            agent,
            messages,
            stream: false,
            tools: toolRegistry.Tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorText = await SafeReadBodyAsync(response, cancellationToken);
            return ProviderAssistantResponse.FromError(
                $"OpenAI-compatible request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenAiCompletion(body);
    }

    private async Task<ProviderAssistantResponse> CompleteOllamaWithToolsAsync(
        HttpClient client,
        string endpoint,
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        ToolRegistry toolRegistry,
        CancellationToken cancellationToken)
    {
        string payload = BuildOllamaPayload(
            modelName,
            agent,
            messages,
            stream: false,
            tools: toolRegistry.Tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorText = await SafeReadBodyAsync(response, cancellationToken);
            return ProviderAssistantResponse.FromError(
                $"Ollama request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOllamaCompletion(body);
    }

    private async Task<ToolExecutionResult> ExecuteToolCallAsync(
        ProviderToolCall toolCall,
        ToolRegistry toolRegistry,
        CancellationToken cancellationToken)
    {
        if (!toolRegistry.ToolsByProviderName.TryGetValue(toolCall.Name, out var tool))
        {
            string errorMessage = $"Tool '{toolCall.Name}' is not registered for this request.";
            logger.LogWarning(errorMessage);

            string errorPayload = JsonSerializer.Serialize(new { error = errorMessage }, JsonOptions);
            return new ToolExecutionResult(
                new ProviderMessage("tool", errorPayload, toolCall.Name, toolCall.Id, null),
                new FunctionCallRecord("unknown", toolCall.Name, toolCall.Arguments, $"status=error;response={errorPayload}"));
        }

        var (arguments, normalizedRequest) = ParseToolArguments(toolCall.Arguments);

        try
        {
            var result = await tool.Tool.CallAsync(arguments, null, null, cancellationToken);
            string responsePayload = SerializeForToolTransport(result);

            logger.LogInformation(
                "Executed MCP tool {Server}:{ToolName} for provider tool {ProviderToolName}",
                tool.ServerName,
                tool.ToolName,
                tool.ProviderName);

            return new ToolExecutionResult(
                new ProviderMessage("tool", responsePayload, tool.ProviderName, toolCall.Id, null),
                new FunctionCallRecord(tool.ServerName, tool.ToolName, normalizedRequest, $"status=ok;response={responsePayload}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "MCP tool execution failed for {Server}:{ToolName}",
                tool.ServerName,
                tool.ToolName);

            string errorPayload = JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
            return new ToolExecutionResult(
                new ProviderMessage("tool", errorPayload, tool.ProviderName, toolCall.Id, null),
                new FunctionCallRecord(tool.ServerName, tool.ToolName, normalizedRequest, $"status=error;response={errorPayload}"));
        }
    }

    private async IAsyncEnumerable<ChatEngineStreamChunk> StreamOllamaAsync(
        LlmServerConfig server,
        AgentDescription agent,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);
        var endpoint = BuildOllamaChatEndpoint(server);
        var payload = BuildOllamaPayload(modelName, agent, messages, stream: true, tools: null);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await SafeReadBodyAsync(response, cancellationToken);
            yield return new ChatEngineStreamChunk(
                agent.AgentName,
                $"Ollama request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        bool completed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryReadOllamaChunk(line, out var content, out var done, out var error))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                yield return new ChatEngineStreamChunk(agent.AgentName, error, IsFinal: true, IsError: true);
                yield break;
            }

            if (!string.IsNullOrEmpty(content))
            {
                yield return new ChatEngineStreamChunk(agent.AgentName, content);
            }

            if (done)
            {
                completed = true;
                break;
            }
        }

        if (!completed)
        {
            logger.LogDebug("Ollama streaming ended without explicit done=true marker for agent {AgentName}.", agent.AgentName);
        }

        yield return new ChatEngineStreamChunk(agent.AgentName, string.Empty, IsFinal: true);
    }

    private async IAsyncEnumerable<ChatEngineStreamChunk> StreamOpenAiCompatibleAsync(
        LlmServerConfig server,
        AgentDescription agent,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string apiKey = ResolveOpenAiApiKey(server);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new ChatEngineStreamChunk(
                agent.AgentName,
                "OpenAI API key is required but not configured.",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        using var client = CreateHttpClient(server, "https://api.openai.com");
        var endpoint = BuildOpenAiChatEndpoint(server);
        var payload = BuildOpenAiPayload(modelName, agent, messages, stream: true, tools: null);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await SafeReadBodyAsync(response, cancellationToken);
            yield return new ChatEngineStreamChunk(
                agent.AgentName,
                $"OpenAI-compatible request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (TryReadOpenAiChunk(data, out var content, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    yield return new ChatEngineStreamChunk(agent.AgentName, error, IsFinal: true, IsError: true);
                    yield break;
                }

                if (!string.IsNullOrEmpty(content))
                {
                    yield return new ChatEngineStreamChunk(agent.AgentName, content);
                }
            }
        }

        yield return new ChatEngineStreamChunk(agent.AgentName, string.Empty, IsFinal: true);
    }

    private static List<ProviderMessage> BuildProviderMessages(AgenticExecutionRuntimeRequest request)
    {
        var result = new List<ProviderMessage>();

        if (!string.IsNullOrWhiteSpace(request.Agent.Content))
        {
            result.Add(new ProviderMessage("system", request.Agent.Content.Trim(), null, null, null));
        }

        foreach (var message in request.Conversation)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add(new ProviderMessage(ToProviderRole(message.Role), text, null, null, null));
        }

        if (!result.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(request.UserMessage))
        {
            result.Add(new ProviderMessage("user", request.UserMessage.Trim(), null, null, null));
        }

        return result;
    }

    private static string ToProviderRole(ChatRole role)
    {
        if (role == ChatRole.User)
            return "user";
        if (role == ChatRole.Assistant)
            return "assistant";
        if (role == ChatRole.System)
            return "system";
        if (role == ChatRole.Tool)
            return "tool";

        return "user";
    }

    private async Task<IReadOnlyList<string>> ResolveRequestedFunctionNamesAsync(
        AgenticExecutionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);

        foreach (var function in request.Configuration.Functions)
        {
            if (string.IsNullOrWhiteSpace(function))
                continue;

            requested.Add(function.Trim());
        }

        try
        {
            var fromAgentSettings = await kernelService.GetFunctionsToRegisterAsync(
                request.Agent.FunctionSettings,
                request.UserMessage,
                cancellationToken);

            foreach (var function in fromAgentSettings)
            {
                if (string.IsNullOrWhiteSpace(function))
                    continue;

                requested.Add(function.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve function selection for agent {AgentName}. Falling back to explicit configuration only.",
                request.Agent.AgentName);
        }

        return requested.ToList();
    }

    private async Task<ToolRegistry> ResolveToolRegistryAsync(
        IReadOnlyCollection<string> requestedFunctions,
        CancellationToken cancellationToken)
    {
        if (requestedFunctions.Count == 0)
        {
            return ToolRegistry.Empty;
        }

        HashSet<string> requestedQualified = new(requestedFunctions, StringComparer.OrdinalIgnoreCase);
        HashSet<string> requestedByToolName = new(
            requestedFunctions.Select(ExtractToolName),
            StringComparer.OrdinalIgnoreCase);

        var clients = await mcpClientService.GetMcpClientsAsync(cancellationToken);
        if (clients.Count == 0)
        {
            return ToolRegistry.Empty;
        }

        List<ToolBinding> tools = [];
        Dictionary<string, ToolBinding> toolsByProviderName = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedProviderToolNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string serverName = client.ServerInfo.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverName))
            {
                logger.LogWarning("Skipping MCP client with empty server name while resolving tools.");
                continue;
            }

            var availableTools = await mcpClientService.GetMcpTools(client, cancellationToken);
            foreach (var tool in availableTools)
            {
                string qualifiedName = $"{serverName}:{tool.Name}";
                bool selected = requestedQualified.Contains(qualifiedName) || requestedByToolName.Contains(tool.Name);
                if (!selected)
                {
                    continue;
                }

                string providerName = CreateProviderToolName(serverName, tool.Name, usedProviderToolNames);
                var binding = new ToolBinding(serverName, tool.Name, providerName, tool);
                tools.Add(binding);
                toolsByProviderName[providerName] = binding;
            }
        }

        return tools.Count == 0
            ? ToolRegistry.Empty
            : new ToolRegistry(tools, toolsByProviderName);
    }

    private static string BuildOllamaPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        bool stream,
        IReadOnlyList<ToolBinding>? tools)
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
            payload["tools"] = BuildProviderToolDefinitions(tools);
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildOpenAiPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages,
        bool stream,
        IReadOnlyList<ToolBinding>? tools)
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
            payload["tools"] = BuildProviderToolDefinitions(tools);
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
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

    private static List<Dictionary<string, object?>> BuildProviderToolDefinitions(IReadOnlyList<ToolBinding> tools)
    {
        List<Dictionary<string, object?>> result = [];

        foreach (var tool in tools)
        {
            JsonElement schema = tool.Tool.JsonSchema.ValueKind == JsonValueKind.Undefined
                ? EmptyToolSchema
                : tool.Tool.JsonSchema.Clone();

            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.ProviderName,
                    ["description"] = tool.Tool.Description,
                    ["parameters"] = schema
                }
            });
        }

        return result;
    }

    private static ProviderAssistantResponse ParseOpenAiCompletion(string json)
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

    private static ProviderAssistantResponse ParseOllamaCompletion(string json)
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

    private static (Dictionary<string, object?> Arguments, string NormalizedRequest) ParseToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return (new Dictionary<string, object?>(), "{}");
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            string normalized = document.RootElement.GetRawText();
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (new Dictionary<string, object?>(), normalized);
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(normalized) ??
                         new Dictionary<string, object?>();

            return (parsed, normalized);
        }
        catch
        {
            return (new Dictionary<string, object?>(), arguments);
        }
    }

    private static string SerializeForToolTransport(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
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

    private static string ExtractToolName(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return string.Empty;
        }

        int separatorIndex = qualifiedName.LastIndexOf(':');
        return separatorIndex >= 0
            ? qualifiedName[(separatorIndex + 1)..]
            : qualifiedName;
    }

    private static string CreateProviderToolName(
        string serverName,
        string toolName,
        HashSet<string> usedNames)
    {
        const int maxLength = 64;
        string baseName = $"{SanitizeToolNamePart(serverName)}__{SanitizeToolNamePart(toolName)}";
        if (baseName.Length > maxLength)
        {
            baseName = baseName[..maxLength];
        }

        string candidate = baseName;
        int suffix = 1;
        while (!usedNames.Add(candidate))
        {
            string suffixText = $"_{suffix++}";
            int prefixLength = Math.Max(1, maxLength - suffixText.Length);
            candidate = $"{baseName[..Math.Min(baseName.Length, prefixLength)]}{suffixText}";
        }

        return candidate;
    }

    private static string SanitizeToolNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }

    private static ProviderMessage ToAssistantMessage(ProviderAssistantResponse completion)
    {
        return completion.ToolCalls.Count == 0
            ? new ProviderMessage("assistant", completion.Content, null, null, null)
            : new ProviderMessage("assistant", completion.Content, null, null, completion.ToolCalls);
    }

    private static bool TryReadOllamaChunk(string json, out string content, out bool done, out string? error)
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

    private static bool TryReadOpenAiChunk(string json, out string content, out string? error)
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

    private string ResolveOpenAiApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
        {
            return server.ApiKey;
        }

        return configuration["OpenAI:ApiKey"] ?? string.Empty;
    }

    private static HttpClient CreateHttpClient(LlmServerConfig server, string defaultBaseUrl)
    {
        var client = LlmServerConfigHelper.CreateHttpClient(server, defaultBaseUrl);
        if (!string.IsNullOrWhiteSpace(server.Password))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{server.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return client;
    }

    private static string BuildOllamaChatEndpoint(LlmServerConfig server)
    {
        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? LlmServerConfig.DefaultOllamaUrl
            : server.BaseUrl;
        return $"{baseUrl.TrimEnd('/')}/api/chat";
    }

    private static string BuildOpenAiChatEndpoint(LlmServerConfig server)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        var baseUrl = server.BaseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat/completions"
            : $"{baseUrl}/v1/chat/completions";
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body) ? "<empty>" : body;
        }
        catch
        {
            return "<failed to read response body>";
        }
    }

    private static JsonElement CreateEmptyToolSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    private sealed record ProviderMessage(
        string Role,
        string? Content,
        string? Name,
        string? ToolCallId,
        IReadOnlyList<ProviderToolCall>? ToolCalls);

    private sealed record ProviderToolCall(string Id, string Name, string Arguments);

    private sealed record ProviderAssistantResponse(
        string Content,
        IReadOnlyList<ProviderToolCall> ToolCalls,
        string? Error = null)
    {
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public static ProviderAssistantResponse FromError(string error) => new(string.Empty, [], error);
    }

    private sealed record ToolBinding(
        string ServerName,
        string ToolName,
        string ProviderName,
        McpClientTool Tool);

    private sealed record ToolRegistry(
        IReadOnlyList<ToolBinding> Tools,
        IReadOnlyDictionary<string, ToolBinding> ToolsByProviderName)
    {
        public static ToolRegistry Empty { get; } = new([], new Dictionary<string, ToolBinding>(StringComparer.OrdinalIgnoreCase));
        public bool HasTools => Tools.Count > 0;
    }

    private sealed record ToolExecutionResult(ProviderMessage ToolMessage, FunctionCallRecord Record);
}
