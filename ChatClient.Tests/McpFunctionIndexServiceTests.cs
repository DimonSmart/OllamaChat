using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ChatClient.Tests;

public class McpFunctionIndexServiceTests
{
    private sealed class DummyMcpClientService : IMcpClientService
    {
        public Task<IReadOnlyCollection<ModelContextProtocol.Client.IMcpClient>> GetMcpClientsAsync() => Task.FromResult<IReadOnlyCollection<ModelContextProtocol.Client.IMcpClient>>(Array.Empty<ModelContextProtocol.Client.IMcpClient>());
        public Task<IReadOnlyList<McpClientTool>> GetMcpTools(ModelContextProtocol.Client.IMcpClient mcpClient) => Task.FromResult<IReadOnlyList<McpClientTool>>(Array.Empty<McpClientTool>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEmbeddingService : IOllamaEmbeddingService
    {
        private readonly Dictionary<string, float[]> _vectors;
        public FakeEmbeddingService(Dictionary<string, float[]> vectors) => _vectors = vectors;
        public Task<float[]> GenerateEmbeddingAsync(string input, string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult(_vectors[input]);
    }

    [Fact]
    public async Task SelectRelevantFunctions_ReturnsBestMatch()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["Tool1. first"] = new[] {1f, 0f},
            ["Tool2. second"] = new[] {0f, 1f},
            ["query"] = new[] {0.9f, 0.1f}
        };
        var indexService = new McpFunctionIndexService(new DummyMcpClientService(), new FakeEmbeddingService(embeddings), NullLogger<McpFunctionIndexService>.Instance);

        // Pre-populate the index via reflection
        var indexField = typeof(McpFunctionIndexService).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, float[]>)indexField.GetValue(indexService)!;
        dict["srv:Tool1"] = embeddings["Tool1. first"];
        dict["srv:Tool2"] = embeddings["Tool2. second"];

        var result = await indexService.SelectRelevantFunctionsAsync("query", 1);
        Assert.Single(result);
        Assert.Equal("Tool1", result[0]);
    }
}
