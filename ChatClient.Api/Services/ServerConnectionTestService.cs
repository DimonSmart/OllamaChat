using ChatClient.Domain.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class ServerConnectionTestService(ILogger<ServerConnectionTestService> logger, IConfiguration configuration) : IServerConnectionTestService
{
    private readonly ILogger<ServerConnectionTestService> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    public async Task<ServerConnectionTestResult> TestConnectionAsync(LlmServerConfig server, CancellationToken cancellationToken = default)
    {
        try
        {
            return server.ServerType switch
            {
                ServerType.Ollama => await TestOllamaConnectionAsync(server, cancellationToken),
                ServerType.ChatGpt => await TestOpenAIConnectionAsync(server, cancellationToken),
                _ => ServerConnectionTestResult.Failure("Unknown server type")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing connection to server {ServerName}", server.Name);
            return ServerConnectionTestResult.Failure($"Connection test failed: {ex.Message}");
        }
    }

    private async Task<ServerConnectionTestResult> TestOllamaConnectionAsync(LlmServerConfig server, CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(server);

        try
        {
            var response = await client.GetAsync("api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Ollama connection test successful. Response: {Response}", content);

            var modelCount = TryParseOllamaModelCount(content);
            return modelCount.HasValue
                ? ServerConnectionTestResult.Success($"Successfully connected to Ollama server. Found {modelCount} available models")
                : ServerConnectionTestResult.Success("Successfully connected to Ollama server");
        }
        catch (HttpRequestException ex)
        {
            return ServerConnectionTestResult.Failure($"HTTP request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return ServerConnectionTestResult.Failure($"Request timed out: {ex.Message}");
        }
    }

    private HttpClient CreateHttpClient(LlmServerConfig server)
    {
        var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl) ? "http://localhost:11434" : server.BaseUrl.TrimEnd('/');

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(server.HttpTimeoutSeconds),
            BaseAddress = new Uri(baseUrl)
        };

        ConfigureOllamaAuthentication(client, server);
        return client;
    }

    private static void ConfigureOllamaAuthentication(HttpClient client, LlmServerConfig server)
    {
        if (!string.IsNullOrEmpty(server.Password))
        {
            var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{server.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
    }

    private static int? TryParseOllamaModelCount(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
            {
                return modelsElement.GetArrayLength();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private string GetEffectiveApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            return server.ApiKey;

        var configApiKey = _configuration["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configApiKey))
        {
            _logger.LogDebug("Using OpenAI API key from configuration (user secrets)");
            return configApiKey;
        }

        return string.Empty;
    }

    private async Task<ServerConnectionTestResult> TestOpenAIConnectionAsync(LlmServerConfig server, CancellationToken cancellationToken)
    {
        var effectiveApiKey = GetEffectiveApiKey(server);
        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            return ServerConnectionTestResult.Failure("API Key is required for OpenAI servers. Set it in server settings or use 'dotnet user-secrets set \"OpenAI:ApiKey\" \"your-key\"'");
        }

        try
        {
            using var httpClient = CreateOpenAIHttpClient(server);
            var fullUrl = BuildOpenAIApiUrl(server);

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", effectiveApiKey);

            _logger.LogDebug("Testing OpenAI connection to URL: {Url}", fullUrl);
            var response = await httpClient.GetAsync(fullUrl, cancellationToken);

            return await ProcessOpenAIResponse(response, fullUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return ServerConnectionTestResult.Failure($"HTTP request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return ServerConnectionTestResult.Failure($"Request timed out: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OpenAI connection test");
            return ServerConnectionTestResult.Failure($"Connection failed: {ex.Message}");
        }
    }

    private HttpClient CreateOpenAIHttpClient(LlmServerConfig server)
    {
        var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(server.HttpTimeoutSeconds)
        };
    }

    private static string BuildOpenAIApiUrl(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            var baseUrl = server.BaseUrl.TrimEnd('/');
            return baseUrl.EndsWith("/v1") ? $"{baseUrl}/models" : $"{baseUrl}/v1/models";
        }

        return "https://api.openai.com/v1/models";
    }

    private async Task<ServerConnectionTestResult> ProcessOpenAIResponse(HttpResponseMessage response, string fullUrl, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("OpenAI connection test successful. Response: {Response}", content);

            var modelCount = TryParseOpenAIModelCount(content);
            return modelCount.HasValue
                ? ServerConnectionTestResult.Success($"Successfully connected to OpenAI server. Found {modelCount} available models")
                : ServerConnectionTestResult.Success("Successfully connected to OpenAI server");
        }

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("OpenAI API returned error. Status: {StatusCode}, Content: {Content}", response.StatusCode, errorContent);

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => ServerConnectionTestResult.Failure("Invalid API key or unauthorized access"),
            System.Net.HttpStatusCode.NotFound => ServerConnectionTestResult.Failure($"API endpoint not found. Check if the base URL is correct. Tried: {fullUrl}"),
            System.Net.HttpStatusCode.TooManyRequests => ServerConnectionTestResult.Failure("Rate limit exceeded"),
            System.Net.HttpStatusCode.InternalServerError => ServerConnectionTestResult.Failure("OpenAI server error"),
            _ => ServerConnectionTestResult.Failure($"HTTP error {(int)response.StatusCode}: {response.ReasonPhrase}. Endpoint: {fullUrl}")
        };
    }

    private static int? TryParseOpenAIModelCount(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                return dataElement.GetArrayLength();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }
}
