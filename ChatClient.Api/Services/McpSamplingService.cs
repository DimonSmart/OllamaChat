using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using DimonSmart.AiUtils;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services;

/// <summary>
/// Handles MCP sampling requests by calling the configured LLM server directly.
/// </summary>
public sealed class McpSamplingService(
    IOllamaClientService ollamaService,
    IOpenAIClientService openAIClientService,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IConfiguration configuration,
    ILogger<McpSamplingService> logger)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<CreateMessageResult> HandleSamplingRequestAsync(
        CreateMessageRequestParams request,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        McpServerConfig? mcpServerConfig = null,
        Guid serverId = default)
    {
        ServerModel? selectedModel = null;
        try
        {
            ValidateRequest(request);
            progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 100 });

            selectedModel = await DetermineModelToUseAsync(
                request.ModelPreferences,
                mcpServerConfig,
                serverId,
                cancellationToken);

            var server = await LlmServerConfigHelper.GetServerConfigAsync(
                             llmServerConfigService,
                             userSettingsService,
                             selectedModel.ServerId)
                         ?? throw new InvalidOperationException("No server configuration found for the selected sampling model.");

            progress?.Report(new ProgressNotificationValue { Progress = 25, Total = 100 });

            var response = await ProcessSamplingRequestAsync(
                request.Messages,
                server,
                selectedModel.ModelName,
                cancellationToken);

            progress?.Report(new ProgressNotificationValue { Progress = 100, Total = 100 });

            return CreateSuccessfulResult(response, selectedModel.ModelName);
        }
        catch (Exception ex)
        {
            await LogAndHandleExceptionAsync(ex, request, mcpServerConfig);
            throw;
        }
    }

    private void ValidateRequest(CreateMessageRequestParams request)
    {
        logger.LogInformation("Processing sampling request with {MessageCount} messages", request.Messages?.Count ?? 0);

        if (request.Messages == null || request.Messages.Count == 0)
        {
            throw new ArgumentException("Sampling request must contain at least one message");
        }
    }

    private async Task<string> ProcessSamplingRequestAsync(
        IList<SamplingMessage> sourceMessages,
        LlmServerConfig server,
        string modelName,
        CancellationToken cancellationToken)
    {
        var messages = ConvertMcpMessagesToProviderMessages(sourceMessages);
        if (messages.Count == 0)
        {
            throw new InvalidOperationException("Sampling request does not contain text messages.");
        }

        if (!string.Equals(messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            messages[^1] = messages[^1] with { Role = "user" };
        }

        return server.ServerType == ServerType.ChatGpt
            ? await CompleteOpenAiAsync(server, modelName, messages, cancellationToken)
            : await CompleteOllamaAsync(server, modelName, messages, cancellationToken);
    }

    private async Task<string> CompleteOllamaAsync(
        LlmServerConfig server,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = messages.Select(static m => new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            }).ToList(),
            ["stream"] = false
        }, _jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildOllamaChatEndpoint(server))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await SafeReadBodyAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Ollama sampling request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOllamaCompletion(body);
    }

    private async Task<string> CompleteOpenAiAsync(
        LlmServerConfig server,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        CancellationToken cancellationToken)
    {
        var apiKey = GetEffectiveOpenAiApiKey(server);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is required but not configured.");
        }

        using var client = CreateHttpClient(server, "https://api.openai.com");

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = messages.Select(static m => new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            }).ToList(),
            ["stream"] = false
        }, _jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildOpenAiChatEndpoint(server))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await SafeReadBodyAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"OpenAI sampling request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {errorText}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenAiCompletion(body);
    }

    private CreateMessageResult CreateSuccessfulResult(string responseText, string modelName)
    {
        var parsed = ThinkTagParser.ExtractThinkAnswer(responseText).Answer;
        logger.LogInformation("Sampling request completed successfully, response length: {Length}", parsed.Length);

        return new CreateMessageResult
        {
            Content = [new TextContentBlock { Text = parsed }],
            Model = modelName,
            StopReason = "endTurn",
            Role = Role.Assistant
        };
    }

    private async Task<ServerModel> DetermineModelToUseAsync(
        ModelPreferences? modelPreferences,
        McpServerConfig? mcpServerConfig,
        Guid explicitServerId,
        CancellationToken cancellationToken)
    {
        var allServers = await llmServerConfigService.GetAllAsync();
        if (allServers.Count == 0)
        {
            throw new InvalidOperationException("No LLM servers configured.");
        }

        var settings = await userSettingsService.GetSettingsAsync(cancellationToken);

        LlmServerConfig? selectedServer = null;
        if (explicitServerId != Guid.Empty)
        {
            selectedServer = allServers.FirstOrDefault(s => s.Id == explicitServerId);
        }

        if (selectedServer is null && settings.DefaultModel.ServerId is Guid defaultServerId && defaultServerId != Guid.Empty)
        {
            selectedServer = allServers.FirstOrDefault(s => s.Id == defaultServerId);
        }

        selectedServer ??= allServers.First();

        var selectedServerId = selectedServer.Id ?? Guid.Empty;
        var availableModelNames = await TryGetAvailableModelNamesAsync(selectedServer, selectedServerId, cancellationToken);
        var availableSet = availableModelNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requestedModel = modelPreferences?.Hints?.FirstOrDefault()?.Name;
        var candidates = new[]
        {
            requestedModel,
            mcpServerConfig?.SamplingModel,
            settings.DefaultModel.ModelName
        }
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Select(static value => value!.Trim())
        .ToList();

        string? modelName = null;
        foreach (var candidate in candidates)
        {
            if (availableSet.Count == 0 || availableSet.Contains(candidate))
            {
                modelName = candidate;
                break;
            }
        }

        if (modelName is null && availableModelNames.Count > 0)
        {
            modelName = availableModelNames[0];
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new InvalidOperationException("No valid model available for MCP sampling.");
        }

        logger.LogInformation(
            "Using model {ModelName} on server {ServerName} for MCP sampling.",
            modelName,
            selectedServer.Name);

        return new ServerModel(selectedServerId, modelName);
    }

    private async Task<IReadOnlyList<string>> TryGetAvailableModelNamesAsync(
        LlmServerConfig server,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (server.ServerType == ServerType.ChatGpt)
            {
                var models = await openAIClientService.GetAvailableModelsAsync(serverId, cancellationToken);
                return models.ToList();
            }

            var ollamaModels = await ollamaService.GetModelsAsync(serverId);
            return ollamaModels.Select(static model => model.Name).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load available models for server {ServerName}", server.Name);
            return [];
        }
    }

    private static List<ProviderMessage> ConvertMcpMessagesToProviderMessages(IEnumerable<SamplingMessage> source)
    {
        var result = new List<ProviderMessage>();
        foreach (var sourceMessage in source)
        {
            var content = string.Join(
                Environment.NewLine,
                sourceMessage.Content
                    .OfType<TextContentBlock>()
                    .Select(static block => block.Text)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var role = sourceMessage.Role switch
            {
                Role.User => "user",
                Role.Assistant => "assistant",
                _ => "user"
            };

            result.Add(new ProviderMessage(role, content.Trim()));
        }

        return result;
    }

    private string ParseOllamaCompletion(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty))
            {
                var error = errorProperty.ValueKind == JsonValueKind.String
                    ? errorProperty.GetString()
                    : errorProperty.GetRawText();
                throw new InvalidOperationException(error ?? "Unknown Ollama error.");
            }

            if (!root.TryGetProperty("message", out var messageProperty) ||
                messageProperty.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Ollama response does not contain a message object.");
            }

            if (!messageProperty.TryGetProperty("content", out var contentProperty) ||
                contentProperty.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return contentProperty.GetString() ?? string.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse Ollama response: {ex.Message}", ex);
        }
    }

    private string ParseOpenAiCompletion(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProperty))
            {
                var message = ReadOpenAiErrorMessage(errorProperty);
                throw new InvalidOperationException(message ?? "Unknown OpenAI error.");
            }

            if (!root.TryGetProperty("choices", out var choicesProperty) ||
                choicesProperty.ValueKind != JsonValueKind.Array ||
                choicesProperty.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("OpenAI response does not contain choices.");
            }

            var firstChoice = choicesProperty[0];
            if (!firstChoice.TryGetProperty("message", out var messageProperty) ||
                messageProperty.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("OpenAI response does not contain a message object.");
            }

            if (!messageProperty.TryGetProperty("content", out var contentProperty))
            {
                return string.Empty;
            }

            return contentProperty.ValueKind switch
            {
                JsonValueKind.String => contentProperty.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(
                    Environment.NewLine,
                    contentProperty.EnumerateArray()
                        .Select(static item => item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                            ? text.GetString()
                            : null)
                        .Where(static text => !string.IsNullOrWhiteSpace(text))),
                _ => string.Empty
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse OpenAI response: {ex.Message}", ex);
        }
    }

    private static string? ReadOpenAiErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var messageProperty) &&
            messageProperty.ValueKind == JsonValueKind.String)
        {
            return messageProperty.GetString();
        }

        return null;
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

    private string GetEffectiveOpenAiApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
        {
            return server.ApiKey;
        }

        return configuration["OpenAI:ApiKey"] ?? string.Empty;
    }

    private async Task LogAndHandleExceptionAsync(Exception ex, CreateMessageRequestParams request, McpServerConfig? mcpServerConfig)
    {
        if (ex is TaskCanceledException or TimeoutException)
        {
            var timeoutSeconds = await GetMcpSamplingTimeoutAsync();
            logger.LogError(
                ex,
                "MCP sampling request timed out after {TimeoutSeconds} seconds. Request had {MessageCount} messages for server: {ServerName}",
                timeoutSeconds,
                request.Messages?.Count ?? 0,
                mcpServerConfig?.Name ?? "Unknown");
            return;
        }

        logger.LogError(ex, "Failed to process MCP sampling request: {Message}", ex.Message);
    }

    private async Task<int> GetMcpSamplingTimeoutAsync()
    {
        var userSettings = await userSettingsService.GetSettingsAsync();
        return userSettings.McpSamplingTimeoutSeconds;
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

    private sealed record ProviderMessage(string Role, string Content);
}
