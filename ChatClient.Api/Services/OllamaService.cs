#pragma warning disable SKEXP0070
using ChatClient.Domain.Models;
using ChatClient.Application.Services;
using OllamaSharp;
using OllamaSharp.Models;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace ChatClient.Api.Services;

public sealed class OllamaService(
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaService> logger) : IOllamaClientService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ClientEntry>>> _clients = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<OllamaModel>> _modelsCache = new();
    private Exception? _embeddingError;

    public async Task<OllamaApiClient> GetClientAsync(Guid serverId)
        => (await _clients.GetOrAdd(serverId, id =>
                new Lazy<Task<ClientEntry>>(() => CreateClientEntryAsync(id))).Value).Client;

    private async Task<ClientEntry> CreateClientEntryAsync(Guid serverId)
    {
        var cfg = await GetServerConfigAsync(serverId);
        var http = BuildHttpClient(cfg);
        var client = new OllamaApiClient(http);
        return new ClientEntry(client, http);
    }

    public bool TryInvalidate(Guid serverId)
    {
        if (_clients.TryRemove(serverId, out var lazy))
        {
            if (lazy.IsValueCreated)
            {
                var task = lazy.Value;
                if (task.IsCompletedSuccessfully)
                    task.Result.Dispose();
            }
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId)
    {
        if (_modelsCache.TryGetValue(serverId, out var cached))
            return cached;

        var client = await GetClientAsync(serverId);
        var models = await client.ListLocalModelsAsync();

        var list = models.Select(m => new OllamaModel
        {
            Name = m.Name,
            ModifiedAt = m.ModifiedAt.ToUniversalTime().ToString("O"),
            Size = m.Size,
            Digest = m.Digest,
            SupportsImages = m.Details?.Families?.Any(f => f.Equals("clip", StringComparison.OrdinalIgnoreCase)) == true,
            SupportsFunctionCalling = SupportsFunctionCalling(m)
        })
        .OrderBy(m => m.Name)
        .ToList();

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

        var client = await GetClientAsync(serverId);
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

    private static bool SupportsFunctionCalling(OllamaSharp.Models.Model model)
        => model.Details?.Families?.Any(f =>
               f.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
               f.Contains("function", StringComparison.OrdinalIgnoreCase)) == true;

    public void Dispose()
    {
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated)
            {
                var task = lazy.Value;
                if (task.IsCompletedSuccessfully)
                    task.Result.Dispose();
            }
        }
        _clients.Clear();
    }

    private sealed class ClientEntry : IDisposable
    {
        public ClientEntry(OllamaApiClient client, HttpClient httpClient)
        {
            Client = client;
            HttpClient = httpClient;
        }

        public OllamaApiClient Client { get; }
        public HttpClient HttpClient { get; }

        public void Dispose()
        {
            Client.Dispose();
            HttpClient.Dispose();
        }
    }
}
