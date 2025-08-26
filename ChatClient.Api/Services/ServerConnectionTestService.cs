using ChatClient.Shared.Models;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
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
        using var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl) ? "http://localhost:11434" : server.BaseUrl.TrimEnd('/');
        
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(server.HttpTimeoutSeconds),
            BaseAddress = new Uri(baseUrl)
        };

        // Настройка авторизации для Ollama
        if (!string.IsNullOrEmpty(server.Password))
        {
            var auth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{server.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        try
        {
            var response = await client.GetAsync("api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Ollama connection test successful. Response: {Response}", content);
            
            // Попробуем распарсить ответ для получения количества моделей
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
                {
                    var modelCount = modelsElement.GetArrayLength();
                    return ServerConnectionTestResult.Success($"Successfully connected to Ollama server. Found {modelCount} available models");
                }
            }
            catch (JsonException)
            {
                // Если не удалось распарсить, просто возвращаем успех
            }
            
            return ServerConnectionTestResult.Success("Successfully connected to Ollama server");
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

    private string GetEffectiveApiKey(LlmServerConfig server)
    {
        // Сначала пытаемся использовать API ключ из настроек сервера
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            return server.ApiKey;

        // Если не указан, пробуем получить из конфигурации (user secrets)
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
            using var handler = new HttpClientHandler();
            if (server.IgnoreSslErrors)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(server.HttpTimeoutSeconds)
            };

            // Определяем правильный URL для тестирования
            string fullUrl;

            if (!string.IsNullOrWhiteSpace(server.BaseUrl))
            {
                var baseUrl = server.BaseUrl.TrimEnd('/');
                // Проверяем, содержит ли BaseUrl уже v1
                if (baseUrl.EndsWith("/v1"))
                {
                    fullUrl = $"{baseUrl}/models";
                }
                else
                {
                    fullUrl = $"{baseUrl}/v1/models";
                }
            }
            else
            {
                // Стандартный OpenAI API endpoint
                fullUrl = "https://api.openai.com/v1/models";
            }

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", effectiveApiKey);

            _logger.LogDebug("Testing OpenAI connection to URL: {Url}", fullUrl);
            var response = await httpClient.GetAsync(fullUrl, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("OpenAI connection test successful. Response: {Response}", content);
                
                // Попробуем распарсить ответ для получения количества моделей
                try
                {
                    using var document = JsonDocument.Parse(content);
                    if (document.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        var modelCount = dataElement.GetArrayLength();
                        return ServerConnectionTestResult.Success($"Successfully connected to OpenAI server. Found {modelCount} available models");
                    }
                }
                catch (JsonException)
                {
                    // Если не удалось распарсить, просто возвращаем успех
                }
                
                return ServerConnectionTestResult.Success("Successfully connected to OpenAI server");
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
}