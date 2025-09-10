using ChatClient.Application.Helpers;
using ChatClient.Domain.Models;
using ChatClient.Application.Services;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;

namespace ChatClient.Api.Services;

public class McpFunctionIndexService
{
    private readonly IMcpClientService _clientService;
    private readonly IOllamaClientService _ollamaService;
    private readonly IConfiguration _configuration;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService;
    private readonly ILogger<McpFunctionIndexService> _logger;
    private ServerModel _model = new(Guid.Empty, string.Empty);
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
    }

    public async Task BuildIndexAsync(CancellationToken cancellationToken = default, Guid? serverId = null)
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

            _model = await DetermineModelAsync();

            var targetServer = serverId ?? _model.ServerId;
            if (!await IsOllamaAvailableAsync(targetServer))
            {
                return;
            }

            await IndexMcpFunctionsAsync(cancellationToken, targetServer);
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

    private async Task<bool> IsOllamaAvailableAsync(Guid? serverId)
    {
        try
        {
            await _ollamaService.GetModelsAsync(serverId ?? Guid.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama is not available. Skipping MCP function indexing.");
            return false;
        }
    }

    private async Task IndexMcpFunctionsAsync(CancellationToken cancellationToken, Guid? serverId)
    {
        var clients = await _clientService.GetMcpClientsAsync(cancellationToken);
        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexClientFunctionsAsync(client, cancellationToken, serverId);
        }
    }

    private async Task IndexClientFunctionsAsync(IMcpClient client, CancellationToken cancellationToken, Guid? serverId)
    {
        var tools = await _clientService.GetMcpTools(client, cancellationToken);
        foreach (var tool in tools)
        {
            await IndexSingleToolAsync(client, tool, serverId, cancellationToken);
        }
    }

    private async Task IndexSingleToolAsync(IMcpClient client, McpClientTool tool, Guid? serverId, CancellationToken cancellationToken)
    {
        string text = $"{tool.Name}. {tool.Description}";
        try
        {
            var model = new ServerModel(serverId ?? _model.ServerId, _model.ModelName);
            var embedding = await _ollamaService.GenerateEmbeddingAsync(text, model, cancellationToken);
            _index[$"{client.ServerInfo.Name}:{tool.Name}"] = embedding;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogError("Embedding model '{Model}' not found. Skipping MCP function indexing.", _model.ModelName);
            throw; // Re-throw to stop the entire indexing process
        }
        catch (Exception ex)
        {
            if (!_ollamaService.EmbeddingsAvailable)
            {
                _logger.LogError(ex, "Embedding service unavailable. Stopping MCP function indexing.");
                throw; // Re-throw to stop the entire indexing process
            }
            _logger.LogError(ex, "Failed to index tool {Name}", tool.Name);
        }
    }

    public void Invalidate()
    {
        _index.Clear();
    }

    private async Task<ServerModel> DetermineModelAsync()
    {
        var settings = await _userSettingsService.GetSettingsAsync();
        return ModelSelectionHelper.GetEffectiveEmbeddingModel(
            settings.Embedding.Model,
            settings.DefaultModel,
            "MCP function indexing",
            _logger);
    }

    public async Task<IReadOnlyList<string>> SelectRelevantFunctionsAsync(string query, int topK, CancellationToken cancellationToken = default, Guid? serverId = null)
    {
        await BuildIndexAsync(cancellationToken, serverId);
        var model = new ServerModel(serverId ?? _model.ServerId, _model.ModelName);
        var queryEmbedding = await _ollamaService.GenerateEmbeddingAsync(query, model, cancellationToken);
        return _index
            .Select(kvp => new { Name = kvp.Key, Score = Dot(queryEmbedding.AsSpan(), kvp.Value) })
            .OrderByDescending(e => e.Score)
            .Take(topK)
            .Select(e => e.Name)
            .ToList();
    }

    public async Task RebuildAsync()
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
