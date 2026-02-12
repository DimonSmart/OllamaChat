using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HttpAgenticExecutionRuntime(
    ILlmServerConfigService llmServerConfigService,
    IUserSettingsService userSettingsService,
    IModelCapabilityService modelCapabilityService,
    IConfiguration configuration,
    ILogger<HttpAgenticExecutionRuntime> logger) : IAgenticExecutionRuntime
{
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

        if (request.Configuration.Functions.Count > 0 || request.Configuration.UseWhiteboard)
        {
            logger.LogInformation(
                "HTTP agentic runtime currently runs without tools. Configured functions: {FunctionCount}, whiteboard: {WhiteboardEnabled}, supportsFunctions: {SupportsFunctions}",
                request.Configuration.Functions.Count,
                request.Configuration.UseWhiteboard,
                supportsFunctions);
        }

        var messages = BuildProviderMessages(request);

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

    private async IAsyncEnumerable<ChatEngineStreamChunk> StreamOllamaAsync(
        LlmServerConfig server,
        AgentDescription agent,
        string modelName,
        IReadOnlyList<ProviderMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(server, LlmServerConfig.DefaultOllamaUrl);
        var endpoint = BuildOllamaChatEndpoint(server);
        var payload = BuildOllamaPayload(modelName, agent, messages);

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
        var payload = BuildOpenAiPayload(modelName, agent, messages);

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
            result.Add(new ProviderMessage("system", request.Agent.Content.Trim()));
        }

        foreach (var message in request.Conversation)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add(new ProviderMessage(ToProviderRole(message.Role), text));
        }

        if (!result.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(request.UserMessage))
        {
            result.Add(new ProviderMessage("user", request.UserMessage.Trim()));
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

    private static string BuildOllamaPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages)
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

        var payload = new
        {
            model = modelName,
            messages,
            stream = true,
            options = options.Count == 0 ? null : options
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildOpenAiPayload(
        string modelName,
        AgentDescription agent,
        IReadOnlyList<ProviderMessage> messages)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (agent.Temperature.HasValue)
        {
            payload["temperature"] = agent.Temperature.Value;
        }

        return JsonSerializer.Serialize(payload);
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

    private sealed record ProviderMessage(string Role, string Content);
}
