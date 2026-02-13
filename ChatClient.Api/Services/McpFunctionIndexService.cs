using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace ChatClient.Api.Services;

public class McpFunctionIndexService
{
    private static readonly Guid McpIndexAgentId = Guid.Parse("d97f3034-81fc-4d88-b3f2-7d90d80d9d6b");

    private readonly IMcpClientService _clientService;
    private readonly IOllamaClientService _ollamaService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService;
    private readonly IRagVectorStore _ragVectorStore;
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
        _clientService = clientService;
        _ollamaService = ollamaService;
        _userSettingsService = userSettingsService;
        _indexBackgroundService = indexBackgroundService;
        _ragVectorStore = ragVectorStore;
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

            await BuildOrUpdatePersistedIndexAsync(targetServer, cancellationToken);
            await LoadIndexFromStoreAsync(cancellationToken);
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

    private async Task BuildOrUpdatePersistedIndexAsync(Guid targetServerId, CancellationToken cancellationToken)
    {
        var clients = await _clientService.GetMcpClientsAsync(cancellationToken);
        var activeServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serverName = NormalizeServerName(client.ServerInfo.Name);
            if (serverName is null)
            {
                _logger.LogWarning("Skipping MCP indexing for a client with empty server name.");
                continue;
            }

            activeServers.Add(serverName);
            var tools = await _clientService.GetMcpTools(client, cancellationToken);
            var orderedTools = tools
                .OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static t => t.Description ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            await IndexSingleServerAsync(serverName, orderedTools, targetServerId, cancellationToken);
        }

        await RemoveStaleServerIndexesAsync(activeServers, cancellationToken);
    }

    private async Task IndexSingleServerAsync(
        string serverName,
        IReadOnlyList<McpClientTool> tools,
        Guid targetServerId,
        CancellationToken cancellationToken)
    {
        var metadata = new RagVectorBuildMetadata(
            SourceHash: ComputeToolsHash(serverName, tools),
            SourceModifiedUtc: DateTime.UtcNow,
            EmbeddingModel: _model.ModelName,
            LineChunkSize: 0,
            ParagraphChunkSize: 0,
            ParagraphOverlap: 0,
            TotalChunks: tools.Count);

        var plan = await _ragVectorStore.BeginIndexingAsync(
            McpIndexAgentId,
            serverName,
            metadata,
            cancellationToken: cancellationToken);

        try
        {
            if (plan.StartIndex > 0)
            {
                _logger.LogInformation(
                    "Resuming MCP tool index for server {ServerName} from tool {Start}/{Total}",
                    serverName,
                    plan.StartIndex,
                    tools.Count);
            }

            for (var i = plan.StartIndex; i < tools.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tool = tools[i];
                var toolName = NormalizeToolName(tool.Name);
                if (toolName is null)
                {
                    _logger.LogWarning("Skipping MCP tool with empty name on server {ServerName}.", serverName);
                    continue;
                }

                var qualifiedFunctionName = BuildQualifiedFunctionName(serverName, toolName);
                var embedding = await _ollamaService.GenerateEmbeddingAsync(
                    BuildToolEmbeddingText(toolName, tool.Description),
                    new ServerModel(targetServerId, _model.ModelName),
                    cancellationToken);

                await _ragVectorStore.UpsertEntryAsync(
                    McpIndexAgentId,
                    serverName,
                    new RagVectorStoreEntry(serverName, i, qualifiedFunctionName, embedding),
                    processedChunks: i + 1,
                    totalChunks: tools.Count,
                    cancellationToken);
            }

            await _ragVectorStore.CompleteIndexingAsync(McpIndexAgentId, serverName, tools.Count, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await _ragVectorStore.MarkIndexingFailedAsync(McpIndexAgentId, serverName, ex.Message, CancellationToken.None);
            _logger.LogError("Embedding model '{Model}' not found. Stopping MCP function indexing.", _model.ModelName);
            throw;
        }
        catch (Exception ex)
        {
            await _ragVectorStore.MarkIndexingFailedAsync(McpIndexAgentId, serverName, ex.Message, CancellationToken.None);
            if (!_ollamaService.EmbeddingsAvailable)
            {
                _logger.LogError(ex, "Embedding service unavailable. Stopping MCP function indexing.");
                throw;
            }

            _logger.LogError(ex, "Failed to index MCP tools for server {ServerName}", serverName);
        }
    }

    private async Task RemoveStaleServerIndexesAsync(
        IReadOnlySet<string> activeServers,
        CancellationToken cancellationToken)
    {
        var persisted = await _ragVectorStore.ReadAgentEntriesAsync(McpIndexAgentId, cancellationToken);
        var persistedServerNames = persisted
            .Select(static entry => entry.FileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var persistedServerName in persistedServerNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activeServers.Contains(persistedServerName))
            {
                continue;
            }

            await _ragVectorStore.RemoveFileAsync(McpIndexAgentId, persistedServerName, cancellationToken);
        }
    }

    private async Task LoadIndexFromStoreAsync(CancellationToken cancellationToken)
    {
        var persistedEntries = await _ragVectorStore.ReadAgentEntriesAsync(McpIndexAgentId, cancellationToken);

        _index.Clear();
        foreach (var entry in persistedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            _index[entry.Text] = entry.Vector;
        }
    }

    private static string ComputeToolsHash(string serverName, IReadOnlyList<McpClientTool> tools)
    {
        var builder = new StringBuilder();
        builder.Append(serverName.Trim());
        builder.Append('\n');

        foreach (var tool in tools)
        {
            builder.Append(NormalizeToolName(tool.Name) ?? string.Empty);
            builder.Append('\n');
            builder.Append((tool.Description ?? string.Empty).Trim());
            builder.Append('\n');
            builder.Append(GetSchemaFingerprint(tool.JsonSchema));
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string GetSchemaFingerprint(JsonElement schema)
    {
        return schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : schema.GetRawText();
    }

    private static string BuildQualifiedFunctionName(string serverName, string toolName) => $"{serverName}:{toolName}";

    private static string BuildToolEmbeddingText(string toolName, string? toolDescription)
    {
        return string.IsNullOrWhiteSpace(toolDescription)
            ? toolName
            : $"{toolName}. {toolDescription}";
    }

    private static string? NormalizeServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return null;
        }

        return serverName.Trim();
    }

    private static string? NormalizeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        return toolName.Trim();
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
