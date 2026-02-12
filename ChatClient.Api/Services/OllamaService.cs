using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using OllamaSharp;
using OllamaSharp.Models;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ChatClient.Api.Services;

public sealed class OllamaService(
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaService> logger) : IOllamaClientService
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<OllamaModel>> _modelsCache = new();
    private Exception? _embeddingError;

    public async Task<OllamaApiClient> GetClientAsync(Guid serverId)
    {
        var cfg = await GetServerConfigAsync(serverId);
        var http = BuildHttpClient(cfg);
        return new OllamaApiClient(http);
    }

    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId)
    {
        if (_modelsCache.TryGetValue(serverId, out var cached))
            return cached;

        var cfg = await GetServerConfigAsync(serverId);
        var http = BuildHttpClient(cfg);
        using var client = new OllamaApiClient(http);
        var models = await client.ListLocalModelsAsync();

        var list = new List<OllamaModel>();
        foreach (var model in models.OrderBy(m => m.Name))
        {
            var supportsFunctionCalling = SupportsFunctionCallingByFamily(model);
            if (!supportsFunctionCalling)
            {
                var fromCapabilities = await TryGetFunctionToolCapabilityAsync(http, model.Name);
                if (fromCapabilities.HasValue)
                {
                    supportsFunctionCalling = fromCapabilities.Value;
                }
            }

            list.Add(new OllamaModel
            {
                Name = model.Name,
                ModifiedAt = model.ModifiedAt.ToUniversalTime().ToString("O"),
                Size = model.Size,
                Digest = model.Digest,
                SupportsImages = model.Details?.Families?.Any(f => f.Equals("clip", StringComparison.OrdinalIgnoreCase)) == true,
                SupportsFunctionCalling = supportsFunctionCalling
            });
        }

        _modelsCache[serverId] = list;
        return list;
    }

    public bool EmbeddingsAvailable => _embeddingError is null;

    public Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken ct = default)
        => GenerateEmbeddingInternalAsync(input, model.ModelName, model.ServerId, ct);

    private async Task<float[]> GenerateEmbeddingInternalAsync(string input, string modelId, Guid serverId, CancellationToken ct)
    {
        if (_embeddingError is not null)
            throw new InvalidOperationException("Embedding service unavailable. Restart the application.", _embeddingError);

        using var client = await GetClientAsync(serverId);
        var req = new EmbedRequest { Model = modelId, Input = new List<string> { input } };

        try
        {
            var resp = await client.EmbedAsync(req, ct);
            return resp.Embeddings.First();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _embeddingError = new InvalidOperationException($"Embedding model '{modelId}' not found on Ollama server.", ex);
            throw _embeddingError;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to generate embedding. Ensure Ollama is running.", ex);
        }
    }

    private async Task<LlmServerConfig> GetServerConfigAsync(Guid serverId)
    {
        logger.LogDebug("Attempting to get Ollama server config for ID: {ServerId}", serverId);

        var cfg = await LlmServerConfigHelper.GetServerConfigAsync(
            llmServerConfigService,
            userSettingsService,
            serverId,
            ServerType.Ollama);

        if (cfg != null)
        {
            logger.LogDebug("Found Ollama server: {ServerName} (ID: {ServerId}, Type: {ServerType})",
                cfg.Name, cfg.Id, cfg.ServerType);
            return cfg;
        }

        logger.LogError("No Ollama server found for ID {ServerId}", serverId);
        throw new InvalidOperationException($"Ollama server with ID {serverId} not found.");
    }

    private HttpClient BuildHttpClient(LlmServerConfig cfg)
    {
        var client = httpClientFactory.CreateClient(cfg.IgnoreSslErrors ? "ollama-insecure" : "ollama");

        var baseUrl = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? LlmServerConfig.DefaultOllamaUrl : cfg.BaseUrl.Trim();
        client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? LlmServerConfig.DefaultOllamaUrl : baseUrl);
        client.Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(cfg.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{cfg.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        return client;
    }

    private static bool SupportsFunctionCallingByFamily(OllamaSharp.Models.Model model)
        => model.Details?.Families?.Any(f =>
               f.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
               f.Contains("function", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<bool?> TryGetFunctionToolCapabilityAsync(HttpClient httpClient, string modelName)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { model = modelName });
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/show")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("capabilities", out var capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var capability in capabilities.EnumerateArray())
            {
                if (capability.ValueKind == JsonValueKind.String &&
                    string.Equals(capability.GetString(), "tools", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to detect tools capability via /api/show for model {ModelName}", modelName);
            return null;
        }
    }
}
