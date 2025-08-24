#pragma warning disable SKEXP0070
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;

using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using OllamaSharp;
using OllamaSharp.Models;
using Microsoft.Extensions.Logging;

namespace ChatClient.Api.Services;

public sealed class OllamaService(
    IConfiguration configuration,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService serverConfigService,
    IServiceProvider serviceProvider,
    ILogger<OllamaService> logger) : IOllamaClientService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, (HttpClient HttpClient, OllamaApiClient Client)> _clients = new();
    private Exception? _embeddingError;

    private async Task<OllamaApiClient> GetOllamaClientAsync(Guid? serverId = null)
    {
        var config = await GetServerConfigAsync(serverId);
        var id = config.Id ?? Guid.Empty;

        if (_clients.TryGetValue(id, out var entry))
            return entry.Client;

        var httpClient = BuildHttpClient(config);
        var client = new OllamaApiClient(httpClient);
        _clients[id] = (httpClient, client);
        return client;
    }

    public Task<OllamaApiClient> GetClientAsync(Guid? serverId = null) => GetOllamaClientAsync(serverId);

    public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid? serverId = null) =>
        GetModelsInternalAsync(serverId);

    public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(ServerModel serverModel) =>
        GetModelsInternalAsync(serverModel.ServerId);

    private async Task<IReadOnlyList<OllamaModel>> GetModelsInternalAsync(Guid? serverId)
    {
        var client = await GetOllamaClientAsync(serverId);
        var models = await client.ListLocalModelsAsync();
        return models.Select(m => new OllamaModel
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
    }

    public bool EmbeddingsAvailable => _embeddingError is null;

    public Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default) =>
        GenerateEmbeddingAsync(input, model.ModelName, model.ServerId, cancellationToken);

    public async Task<float[]> GenerateEmbeddingAsync(string input, string modelId, Guid? serverId = null, CancellationToken cancellationToken = default)
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

    private async Task<LlmServerConfig> GetServerConfigAsync(Guid? serverId)
    {
        if (serverId.HasValue && serverId.Value != Guid.Empty)
        {
            var config = await serverConfigService.GetByIdAsync(serverId.Value);
            if (config is not null)
            {
                config.Id ??= serverId.Value;
                return config;
            }

            logger.LogWarning("Server {ServerId} not found. Using default configuration.", serverId);
        }

        var settings = await userSettingsService.GetSettingsAsync();
        if (settings.DefaultLlmId.HasValue)
        {
            var cfg = await serverConfigService.GetByIdAsync(settings.DefaultLlmId.Value);
            if (cfg is not null)
            {
                cfg.Id ??= settings.DefaultLlmId.Value;
                return cfg;
            }
        }

        var baseUrl = !string.IsNullOrWhiteSpace(settings.OllamaServerUrl)
            ? settings.OllamaServerUrl
            : configuration["Ollama:BaseUrl"] ?? OllamaDefaults.ServerUrl;

        return new LlmServerConfig
        {
            Id = Guid.Empty,
            BaseUrl = baseUrl,
            Password = settings.OllamaBasicAuthPassword,
            IgnoreSslErrors = settings.IgnoreSslErrors,
            HttpTimeoutSeconds = settings.HttpTimeoutSeconds,
            ServerType = ServerType.Ollama,
            Name = "Default"
        };
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

        var client = new HttpClient(loggingHandler)
        {
            Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds),
            BaseAddress = new Uri(config.BaseUrl)
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
        foreach (var entry in _clients.Values)
        {
            entry.Client.Dispose();
            entry.HttpClient.Dispose();
        }
    }
}

