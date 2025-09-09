#pragma warning disable SKEXP0070
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using OllamaSharp;
using OllamaSharp.Models;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace ChatClient.Api.Services;

public sealed class OllamaService(
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IServiceProvider serviceProvider,
    ILogger<OllamaService> logger) : IOllamaClientService, IDisposable
{
    private readonly Dictionary<Guid, (OllamaApiClient Client, HttpClient HttpClient)> _clients = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<OllamaModel>> _modelsCache = new();
    private readonly object _lock = new();
    private Exception? _embeddingError;

    private async Task<OllamaApiClient> GetOllamaClientAsync(Guid serverId)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(serverId, out var entry))
                return entry.Client;
        }

        var config = await GetServerConfigAsync(serverId);
        var httpClient = BuildHttpClient(config);
        var client = new OllamaApiClient(httpClient);

        lock (_lock)
        {
            _clients[serverId] = (client, httpClient);
        }
        return client;
    }

    public async Task<OllamaApiClient> GetClientAsync(Guid serverId)
    {
        return await GetOllamaClientAsync(serverId);
    }

    public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId) =>
        GetModelsInternalAsync(serverId);

    private async Task<IReadOnlyList<OllamaModel>> GetModelsInternalAsync(Guid serverId)
    {
        if (_modelsCache.TryGetValue(serverId, out var cached))
            return cached;

        var client = await GetOllamaClientAsync(serverId);
        var models = await client.ListLocalModelsAsync();
        var list = models.Select(m => new OllamaModel
        {
            Name = m.Name,
            ModifiedAt = m.ModifiedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Size = m.Size,
            Digest = m.Digest,
            SupportsImages = m.Details?.Families?.Contains("clip") == true,
            SupportsFunctionCalling = DeterminesFunctionCallingSupport(m)
        })
        .OrderBy(m => m.Name)
        .ToList();

        _modelsCache[serverId] = list;
        return list;
    }

    public bool EmbeddingsAvailable => _embeddingError is null;

    public Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default) =>
        GenerateEmbeddingInternalAsync(input, model.ModelName, model.ServerId, cancellationToken);

    private async Task<float[]> GenerateEmbeddingInternalAsync(string input, string modelId, Guid serverId, CancellationToken cancellationToken = default)
    {
        if (_embeddingError is not null)
            throw new InvalidOperationException("Embedding service unavailable. Restart the application.", _embeddingError);

        var client = await GetOllamaClientAsync(serverId);

        var request = new EmbedRequest { Model = modelId, Input = new List<string> { input } };
        try
        {
            var response = await client.EmbedAsync(request, cancellationToken);
            return response.Embeddings.First();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _embeddingError = new InvalidOperationException($"Embedding model '{modelId}' not found on Ollama server.", ex);
            throw _embeddingError;
        }
        catch (Exception ex)
        {
            _embeddingError = new InvalidOperationException("Failed to generate embedding. Ensure Ollama is running and restart the application.", ex);
            throw _embeddingError;
        }
    }

    private async Task<LlmServerConfig> GetServerConfigAsync(Guid serverId)
    {
        logger.LogDebug("Attempting to get Ollama server config for ID: {ServerId}", serverId);

        var serverConfig = await LlmServerConfigHelper.GetServerConfigAsync(
            llmServerConfigService,
            userSettingsService,
            serverId,
            ServerType.Ollama);

        if (serverConfig != null)
        {
            logger.LogDebug("Found Ollama server: {ServerName} (ID: {ServerId}, Type: {ServerType})",
                serverConfig.Name, serverConfig.Id, serverConfig.ServerType);
            return serverConfig;
        }

        logger.LogError("No Ollama server found for ID {ServerId}", serverId);
        throw new InvalidOperationException($"Ollama server with ID {serverId} not found.");
    }

    private HttpClient BuildHttpClient(LlmServerConfig config)
    {
        var handler = new HttpClientHandler();
        if (config.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var loggingHandler = new HttpLoggingHandler(serviceProvider.GetRequiredService<ILogger<HttpLoggingHandler>>())
        {
            InnerHandler = handler
        };

        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? LlmServerConfig.DefaultOllamaUrl : config.BaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = LlmServerConfig.DefaultOllamaUrl;
        }
        var client = new HttpClient(loggingHandler)
        {
            Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds),
            BaseAddress = new Uri(baseUrl)
        };

        if (!string.IsNullOrWhiteSpace(config.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{config.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        return client;
    }

    private static bool DeterminesFunctionCallingSupport(OllamaSharp.Models.Model model)
    {
        if (model.Details?.Families == null)
            return false;

        return model.Details.Families.Any(family =>
            family.ToLowerInvariant().Contains("tool") ||
            family.ToLowerInvariant().Contains("function"));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _clients.Values)
            {
                entry.Client.Dispose();
                entry.HttpClient.Dispose();
            }
        }
    }
}

