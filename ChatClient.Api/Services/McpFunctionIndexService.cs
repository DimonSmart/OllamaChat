using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Domain.Models;
using System.Collections.Concurrent;
using System.Linq;

namespace ChatClient.Api.Services;

public class McpFunctionIndexService
{
    private readonly IOllamaClientService _ollamaService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService;
    private readonly IRagVectorStore _ragVectorStore;
    private readonly McpFunctionIndexBuilder _indexBuilder;
    private readonly ILogger<McpFunctionIndexService> _logger;
    private ServerModel _model = new(Guid.Empty, string.Empty);
    private readonly ConcurrentDictionary<string, float[]> _index = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public McpFunctionIndexService(
        IMcpClientService clientService,
        IOllamaClientService ollamaService,
        IUserSettingsService userSettingsService,
        IRagVectorIndexBackgroundService indexBackgroundService,
        IRagVectorStore ragVectorStore,
        ILogger<McpFunctionIndexService> logger)
    {
        _ollamaService = ollamaService;
        _userSettingsService = userSettingsService;
        _indexBackgroundService = indexBackgroundService;
        _ragVectorStore = ragVectorStore;
        _logger = logger;
        _indexBuilder = new McpFunctionIndexBuilder(clientService, ollamaService, logger);
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

            await _indexBuilder.BuildAsync(_index, _model, targetServer, cancellationToken);
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
        await _ragVectorStore.ClearAllAsync();

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
