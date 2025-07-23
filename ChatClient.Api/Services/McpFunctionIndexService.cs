using System.Collections.Concurrent;
using System.Linq;

using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class McpFunctionIndexService
{
    private readonly IMcpClientService _clientService;
    private readonly IOllamaClientService _ollamaService;
    private readonly ILogger<McpFunctionIndexService> _logger;
    private readonly string _modelId = "nomic-embed-text";
    private readonly ConcurrentDictionary<string, float[]> _index = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public McpFunctionIndexService(IMcpClientService clientService, IOllamaClientService ollamaService, ILogger<McpFunctionIndexService> logger)
    {
        _clientService = clientService;
        _ollamaService = ollamaService;
        _logger = logger;
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

            var clients = await _clientService.GetMcpClientsAsync();
            foreach (var client in clients)
            {
                var tools = await _clientService.GetMcpTools(client);
                foreach (var tool in tools)
                {
                    string text = $"{tool.Name}. {tool.Description}";
                    try
                    {
                        var embedding = await _ollamaService.GenerateEmbeddingAsync(text, _modelId, cancellationToken);
                        _index[$"{client.ServerInfo.Name}:{tool.Name}"] = embedding;
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

    public async Task<IReadOnlyList<string>> SelectRelevantFunctionsAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        await BuildIndexAsync(cancellationToken);
        var queryEmbedding = await _ollamaService.GenerateEmbeddingAsync(query, _modelId, cancellationToken);
        return _index
            .Select(kvp => new { Name = kvp.Key.Split(':').Last(), Score = Dot(queryEmbedding.AsSpan(), kvp.Value) })
            .OrderByDescending(e => e.Score)
            .Take(topK)
            .Select(e => e.Name)
            .ToList();
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
