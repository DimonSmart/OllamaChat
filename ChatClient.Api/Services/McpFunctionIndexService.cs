using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.IO;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Configuration;

namespace ChatClient.Api.Services;

public class McpFunctionIndexService
{
    private readonly IMcpClientService _clientService;
    private readonly IOllamaClientService _ollamaService;
    private readonly IConfiguration _configuration;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService;
    private readonly ILogger<McpFunctionIndexService> _logger;
    private string _modelId = "nomic-embed-text";
    private readonly ConcurrentDictionary<string, float[]> _index = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public McpFunctionIndexService(
        IMcpClientService clientService,
        IOllamaClientService ollamaService,
        IConfiguration configuration,
        IUserSettingsService userSettingsService,
        IRagVectorIndexBackgroundService indexBackgroundService,
        ILogger<McpFunctionIndexService> logger)
    {
        _clientService = clientService;
        _ollamaService = ollamaService;
        _configuration = configuration;
        _userSettingsService = userSettingsService;
        _indexBackgroundService = indexBackgroundService;
        _logger = logger;

        _userSettingsService.EmbeddingModelChanged += HandleEmbeddingModelChangeAsync;
    }

    public async Task BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_index.Count > 0)
        {
            return;
        }

        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            if (_index.Count > 0)
            {
                return;
            }

            _modelId = await DetermineModelIdAsync();

            try
            {
                await _ollamaService.GetModelsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ollama is not available. Skipping MCP function indexing.");
                return;
            }

            var clients = await _clientService.GetMcpClientsAsync(cancellationToken);
            foreach (var client in clients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tools = await _clientService.GetMcpTools(client, cancellationToken);
                foreach (var tool in tools)
                {
                    string text = $"{tool.Name}. {tool.Description}";
                    try
                    {
                        var embedding = await _ollamaService.GenerateEmbeddingAsync(text, _modelId, cancellationToken);
                        _index[$"{client.ServerInfo.Name}:{tool.Name}"] = embedding;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.LogError("Embedding model '{Model}' not found. Skipping MCP function indexing.", _modelId);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to index tool {Name}", tool.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building MCP function index");
        }
        finally
        {
            _buildLock.Release();
        }
    }

    public void Invalidate()
    {
        _index.Clear();
    }

    private async Task<string> DetermineModelIdAsync()
    {
        var settings = await _userSettingsService.GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(settings.EmbeddingModelName))
        {
            return settings.EmbeddingModelName;
        }

        return _configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    }

    public async Task<IReadOnlyList<string>> SelectRelevantFunctionsAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        await BuildIndexAsync(cancellationToken);
        var queryEmbedding = await _ollamaService.GenerateEmbeddingAsync(query, _modelId, cancellationToken);
        return _index
            .Select(kvp => new { Name = kvp.Key, Score = Dot(queryEmbedding.AsSpan(), kvp.Value) })
            .OrderByDescending(e => e.Score)
            .Take(topK)
            .Select(e => e.Name)
            .ToList();
    }

    private async Task HandleEmbeddingModelChangeAsync()
    {
        var basePath = _configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
        if (Directory.Exists(basePath))
        {
            foreach (var file in Directory.GetFiles(basePath, "*.idx", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete index {File}", file);
                }
            }
        }

        Invalidate();
        await BuildIndexAsync();
        _indexBackgroundService.RequestRebuild();
    }

    private static float Dot(ReadOnlySpan<float> a, IReadOnlyList<float> b)
    {
        int len = Math.Min(a.Length, b.Count);
        float sum = 0f;
        for (int i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }
}
