using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HttpAgenticExecutionRuntime(
    ILlmServerConfigService llmServerConfigService,
    IModelCapabilityService modelCapabilityService,
    IMcpClientService mcpClientService,
    IMcpUserInteractionService mcpUserInteractionService,
    KernelService kernelService,
    IConfiguration configuration,
    IOptions<ChatEngineOptions> chatEngineOptions,
    ILogger<HttpAgenticExecutionRuntime> logger) : IAgenticExecutionRuntime
{
    private const int MaxToolRounds = 8;
    private const int MaxLoggedPayloadLength = 4000;
    private const int MaxNameLookupReminders = 2;
    private const string UserProfilePrefsToolName = "prefs_get";
    private const string UserProfilePrefsGetAllToolName = "prefs_get_all";
    private const string UserProfileDisplayNameKey = "displayName";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonElement EmptyToolSchema = CreateEmptyToolSchema();
    private readonly AgenticToolInvocationPolicyOptions _toolPolicy = NormalizeToolPolicy(chatEngineOptions.Value.ToolPolicy);

    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgenticExecutionRuntimeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolvedModel = request.ResolvedModel;
        string modelName = resolvedModel.ModelName;
        LlmServerConfig? server = await llmServerConfigService.GetByIdAsync(resolvedModel.ServerId);

        if (server is null)
        {
            yield return new ChatEngineStreamChunk(
                request.Agent.AgentName,
                $"Configured LLM server '{resolvedModel.ServerId}' was not found.",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        bool supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
            resolvedModel,
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

        var messages = BuildProviderMessages(request);
        var toolRegistry = supportsFunctions
            ? await ResolveToolRegistryAsync(
                requestedFunctions,
                request.Whiteboard,
                request.Configuration.UseWhiteboard,
                cancellationToken)
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
        var requiresNameLookup = RequiresNameLookupBeforeReply(agent, toolRegistry);
        var hasNameLookup = false;
        var nameLookupReminderCount = 0;

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

            client = LlmChatEndpointHelper.CreateHttpClient(server, "https://api.openai.com");
            endpoint = LlmChatEndpointHelper.BuildOpenAiChatEndpoint(server);
        }
        else
        {
            client = LlmChatEndpointHelper.CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);
            endpoint = LlmChatEndpointHelper.BuildOllamaChatEndpoint(server);
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

                if (completion.ToolCalls.Count == 0)
                {
                    if (requiresNameLookup && !hasNameLookup && nameLookupReminderCount < MaxNameLookupReminders)
                    {
                        nameLookupReminderCount++;
                        messages.Add(new ProviderMessage(
                            "system",
                            "Before answering, call tool `prefs_get` with argument {\"key\":\"displayName\"} to get the user's preferred name. Then answer and address the user by that name.",
                            null,
                            null,
                            null));

                        logger.LogDebug(
                            "Enforcing profile name lookup for agent {AgentName}. Reminder {Reminder}/{MaxReminders}.",
                            agent.AgentName,
                            nameLookupReminderCount,
                            MaxNameLookupReminders);
                        continue;
                    }

                    messages.Add(ToAssistantMessage(completion));

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

                messages.Add(ToAssistantMessage(completion));

                foreach (var toolCall in completion.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var execution = await ExecuteToolCallAsync(toolCall, toolRegistry, cancellationToken);
                    functionCalls.Add(execution.Record);
                    messages.Add(execution.ToolMessage);

                    if (!hasNameLookup && IsNameLookupCall(execution.Record))
                    {
                        hasNameLookup = true;
                    }
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
        string payload = AgenticProviderPayloadBuilder.BuildOpenAiPayload(
            modelName,
            agent,
            messages,
            stream: false,
            tools: toolRegistry.Tools,
            JsonOptions,
            EmptyToolSchema);

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
        return AgenticProviderResponseParser.ParseOpenAiCompletion(body);
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
        string payload = AgenticProviderPayloadBuilder.BuildOllamaPayload(
            modelName,
            agent,
            messages,
            stream: false,
            tools: toolRegistry.Tools,
            JsonOptions,
            EmptyToolSchema);

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
        return AgenticProviderResponseParser.ParseOllamaCompletion(body);
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

        if (!ToolArgumentSchemaValidator.TryValidateAndParse(
                toolCall.Arguments,
                tool.JsonSchema,
                out var arguments,
                out var normalizedRequest,
                out var validationError))
        {
            logger.LogWarning(
                "Tool argument validation failed for {Server}:{ToolName}. Provider name: {ProviderToolName}. Error: {Error}. Arguments: {Arguments}",
                tool.ServerName,
                tool.ToolName,
                tool.ProviderName,
                validationError,
                FormatForLog(toolCall.Arguments, MaxLoggedPayloadLength));

            string errorPayload = JsonSerializer.Serialize(new { error = validationError }, JsonOptions);
            return new ToolExecutionResult(
                new ProviderMessage("tool", errorPayload, tool.ProviderName, toolCall.Id, null),
                new FunctionCallRecord(
                    tool.ServerName,
                    tool.ToolName,
                    normalizedRequest,
                    $"status=validation_error;attempt=1;durationMs=0;response={errorPayload}"));
        }

        int maxAttempts = Math.Max(1, _toolPolicy.MaxRetries + 1);
        Exception? lastException = null;
        var requestForLog = FormatForLog(normalizedRequest, MaxLoggedPayloadLength);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var startedAt = DateTime.UtcNow;
            if (attempt == 1)
            {
                logger.LogInformation(
                    "Calling MCP tool {Server}:{ToolName} for provider tool {ProviderToolName}. Arguments: {Arguments}",
                    tool.ServerName,
                    tool.ToolName,
                    tool.ProviderName,
                    requestForLog);
            }
            else
            {
                logger.LogInformation(
                    "Retrying MCP tool {Server}:{ToolName} for provider tool {ProviderToolName} (attempt {Attempt}/{MaxAttempts}).",
                    tool.ServerName,
                    tool.ToolName,
                    tool.ProviderName,
                    attempt,
                    maxAttempts);
            }

            try
            {
                var timeoutSeconds = tool.MayRequireUserInput
                    ? Math.Max(_toolPolicy.TimeoutSeconds, _toolPolicy.InteractiveTimeoutSeconds)
                    : _toolPolicy.TimeoutSeconds;

                using var timeoutCts = timeoutSeconds > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                if (timeoutSeconds > 0)
                {
                    timeoutCts!.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                }

                var executionToken = timeoutCts?.Token ?? cancellationToken;
                using var interactionScope = mcpUserInteractionService.BeginInteractionScope(McpInteractionScope.Chat);
                var result = await tool.ExecuteAsync(arguments, executionToken);
                string responsePayload = AgenticToolUtility.SerializeForToolTransport(result, JsonOptions);
                int durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);

                logger.LogInformation(
                    "MCP tool {Server}:{ToolName} completed for provider tool {ProviderToolName} (attempt {Attempt}/{MaxAttempts}, {DurationMs} ms). Response: {Response}",
                    tool.ServerName,
                    tool.ToolName,
                    tool.ProviderName,
                    attempt,
                    maxAttempts,
                    durationMs,
                    FormatForLog(responsePayload, MaxLoggedPayloadLength));

                return new ToolExecutionResult(
                    new ProviderMessage("tool", responsePayload, tool.ProviderName, toolCall.Id, null),
                    new FunctionCallRecord(
                        tool.ServerName,
                        tool.ToolName,
                        normalizedRequest,
                        $"status=ok;attempt={attempt};durationMs={durationMs};response={responsePayload}"));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                int durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                logger.LogWarning(
                    "Tool {Server}:{ToolName} timed out on attempt {Attempt}/{MaxAttempts} after {DurationMs} ms.",
                    tool.ServerName,
                    tool.ToolName,
                    attempt,
                    maxAttempts,
                    durationMs);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                int durationMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                logger.LogWarning(
                    ex,
                    "Tool {Server}:{ToolName} failed on attempt {Attempt}/{MaxAttempts} after {DurationMs} ms.",
                    tool.ServerName,
                    tool.ToolName,
                    attempt,
                    maxAttempts,
                    durationMs);
            }

            if (attempt < maxAttempts && _toolPolicy.RetryDelayMs > 0)
            {
                await Task.Delay(_toolPolicy.RetryDelayMs, cancellationToken);
            }
        }

        string finalMessage = lastException?.Message ?? "Unknown tool execution failure.";
        string finalPayload = JsonSerializer.Serialize(new { error = finalMessage }, JsonOptions);
        logger.LogError(
            lastException,
            "Tool execution failed for {Server}:{ToolName} (provider tool {ProviderToolName}) after {MaxAttempts} attempts. Arguments: {Arguments}",
            tool.ServerName,
            tool.ToolName,
            tool.ProviderName,
            maxAttempts,
            requestForLog);

        return new ToolExecutionResult(
            new ProviderMessage("tool", finalPayload, tool.ProviderName, toolCall.Id, null),
            new FunctionCallRecord(
                tool.ServerName,
                tool.ToolName,
                normalizedRequest,
                $"status=error;attempt={maxAttempts};response={finalPayload}"));
    }

    private async IAsyncEnumerable<ChatEngineStreamChunk> StreamOllamaAsync(
        LlmServerConfig server,
        AgentDescription agent,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = LlmChatEndpointHelper.CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);
        var endpoint = LlmChatEndpointHelper.BuildOllamaChatEndpoint(server);
        var payload = AgenticProviderPayloadBuilder.BuildOllamaPayload(
            modelName,
            agent,
            messages,
            stream: true,
            tools: null,
            JsonOptions,
            EmptyToolSchema);

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

            if (!AgenticProviderResponseParser.TryReadOllamaChunk(line, out var content, out var done, out var error))
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

        using var client = LlmChatEndpointHelper.CreateHttpClient(server, "https://api.openai.com");
        var endpoint = LlmChatEndpointHelper.BuildOpenAiChatEndpoint(server);
        var payload = AgenticProviderPayloadBuilder.BuildOpenAiPayload(
            modelName,
            agent,
            messages,
            stream: true,
            tools: null,
            JsonOptions,
            EmptyToolSchema);

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

            if (AgenticProviderResponseParser.TryReadOpenAiChunk(data, out var content, out var error))
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
        WhiteboardState? whiteboard,
        bool useWhiteboard,
        CancellationToken cancellationToken)
    {
        List<ToolBinding> tools = [];
        Dictionary<string, ToolBinding> toolsByProviderName = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedProviderToolNames = new(StringComparer.OrdinalIgnoreCase);

        if (requestedFunctions.Count > 0)
        {
            HashSet<string> requestedQualified = new(requestedFunctions, StringComparer.OrdinalIgnoreCase);
            HashSet<string> requestedByToolName = new(
                requestedFunctions.Select(AgenticToolUtility.ExtractToolName),
                StringComparer.OrdinalIgnoreCase);

            var clients = await mcpClientService.GetMcpClientsAsync(cancellationToken);
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

                    string providerName = AgenticToolUtility.CreateProviderToolName(serverName, tool.Name, usedProviderToolNames);
                    var schema = tool.JsonSchema.ValueKind == JsonValueKind.Undefined
                        ? EmptyToolSchema
                        : tool.JsonSchema.Clone();

                    var binding = new ToolBinding(
                        serverName,
                        tool.Name,
                        providerName,
                        tool.Description ?? string.Empty,
                        schema,
                        async (arguments, token) => await tool.CallAsync(arguments, null, null, token),
                        MayRequireUserInput: MayRequireUserInput(tool.Description));

                    tools.Add(binding);
                    toolsByProviderName[providerName] = binding;
                }
            }
        }

        if (useWhiteboard && whiteboard is not null)
        {
            foreach (var whiteboardTool in BuildWhiteboardTools(whiteboard, usedProviderToolNames))
            {
                tools.Add(whiteboardTool);
                toolsByProviderName[whiteboardTool.ProviderName] = whiteboardTool;
            }
        }

        return tools.Count == 0
            ? ToolRegistry.Empty
            : new ToolRegistry(tools, toolsByProviderName);
    }

    private static IReadOnlyList<ToolBinding> BuildWhiteboardTools(
        WhiteboardState whiteboard,
        HashSet<string> usedProviderToolNames)
    {
        var addNoteSchema = AgenticToolUtility.ParseToolSchema("""
            {
              "type": "object",
              "properties": {
                "note": { "type": "string" },
                "author": { "type": ["string", "null"] }
              },
              "required": ["note"],
              "additionalProperties": false
            }
            """);

        var emptySchema = AgenticToolUtility.ParseToolSchema("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """);

        var addNoteProviderName = AgenticToolUtility.CreateProviderToolName("whiteboard", "add_note", usedProviderToolNames);
        var getNotesProviderName = AgenticToolUtility.CreateProviderToolName("whiteboard", "get_notes", usedProviderToolNames);
        var clearProviderName = AgenticToolUtility.CreateProviderToolName("whiteboard", "clear", usedProviderToolNames);

        return
        [
            new ToolBinding(
                "whiteboard",
                "add_note",
                addNoteProviderName,
                "Add or update a note on the shared whiteboard for this chat session.",
                addNoteSchema,
                (arguments, _) =>
                {
                    var note = AgenticToolUtility.ReadRequiredStringArgument(arguments, "note");
                    var author = AgenticToolUtility.ReadOptionalStringArgument(arguments, "author");
                    whiteboard.Add(note, author);
                    return Task.FromResult<object>(AgenticToolUtility.BuildWhiteboardSnapshot(whiteboard));
                }),
            new ToolBinding(
                "whiteboard",
                "get_notes",
                getNotesProviderName,
                "Return all whiteboard notes as a markdown list.",
                emptySchema,
                (_, _) => Task.FromResult<object>(AgenticToolUtility.BuildWhiteboardSnapshot(whiteboard))),
            new ToolBinding(
                "whiteboard",
                "clear",
                clearProviderName,
                "Clear every note from the shared whiteboard.",
                emptySchema,
                (_, _) =>
                {
                    whiteboard.Clear();
                    return Task.FromResult<object>("Whiteboard cleared.");
                })
        ];
    }

    private static ProviderMessage ToAssistantMessage(ProviderAssistantResponse completion)
    {
        return completion.ToolCalls.Count == 0
            ? new ProviderMessage("assistant", completion.Content, null, null, null)
            : new ProviderMessage("assistant", completion.Content, null, null, completion.ToolCalls);
    }

    private string ResolveOpenAiApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
        {
            return server.ApiKey;
        }

        return configuration["OpenAI:ApiKey"] ?? string.Empty;
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

    private static AgenticToolInvocationPolicyOptions NormalizeToolPolicy(AgenticToolInvocationPolicyOptions? policy)
    {
        policy ??= new AgenticToolInvocationPolicyOptions();

        return new AgenticToolInvocationPolicyOptions
        {
            TimeoutSeconds = Math.Max(0, policy.TimeoutSeconds),
            InteractiveTimeoutSeconds = Math.Max(0, policy.InteractiveTimeoutSeconds),
            MaxRetries = Math.Max(0, policy.MaxRetries),
            RetryDelayMs = Math.Max(0, policy.RetryDelayMs)
        };
    }

    private static bool MayRequireUserInput(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        return description.Contains("elicitation", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("ask user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("asks user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("asks the user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement CreateEmptyToolSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    private static string FormatForLog(string? payload, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "<empty>";

        var singleLine = payload.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= maxLength)
            return singleLine;

        return $"{singleLine[..maxLength]}... (truncated, {singleLine.Length} chars)";
    }

    private static bool RequiresNameLookupBeforeReply(AgentDescription agent, ToolRegistry toolRegistry)
    {
        if (!toolRegistry.HasTools)
            return false;

        var prompt = agent.Content ?? string.Empty;
        var promptRequiresName = prompt.Contains("preferred name", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("address the user by", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("обращай", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("по имени", StringComparison.OrdinalIgnoreCase);
        if (!promptRequiresName)
            return false;

        return toolRegistry.Tools.Any(static tool =>
            string.Equals(tool.ToolName, UserProfilePrefsToolName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNameLookupCall(FunctionCallRecord record)
    {
        if (string.Equals(record.Function, UserProfilePrefsGetAllToolName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(record.Function, UserProfilePrefsToolName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(record.Request))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(record.Request);
            if (!doc.RootElement.TryGetProperty("key", out var keyProperty) ||
                keyProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var key = keyProperty.GetString();
            if (string.IsNullOrWhiteSpace(key))
                return false;

            return string.Equals(key, UserProfileDisplayNameKey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "preferred_name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "preferredName", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "userName", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
