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
        // Very naive embedding based on keyword counts so the test simulates
        // real embedding behavior without external dependencies.
        private static readonly string[] TransistorKeywords = new[] { "transistor", "semiconductor" };
        private static readonly string[] CookingKeywords = new[] { "recipe", "cook", "bake", "pasta", "chocolate", "onions", "chicken", "dish", "oven" };

        public Task<float[]> GenerateEmbeddingAsync(string input, string modelId, CancellationToken cancellationToken = default)
        {
            float tCount = CountKeywords(input, TransistorKeywords);
            float cCount = CountKeywords(input, CookingKeywords);
            return Task.FromResult(new[] { tCount, cCount });
        }

        private static int CountKeywords(string input, IEnumerable<string> keywords)
        {
            var count = 0;
            foreach (var word in keywords)
            {
                if (input.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }
    }

    [Fact]
    public async Task SelectRelevantFunctions_FindsTransistorEntries()
    {
        var lines = new List<(string Name, string Text)>
        {
            ("TransTool1", "Transistors are semiconductor devices used to amplify signals."),
            ("TransTool2", "A transistor can act as a switch in electronic circuits."),
            ("CookTool1", "To bake a cake, start by preheating your oven."),
            ("CookTool2", "This recipe uses fresh tomatoes and basil."),
            ("CookTool3", "Heat oil in a pan and add chopped onions."),
            ("CookTool4", "Add the pasta and cook until al dente."),
            ("CookTool5", "Mix chocolate with butter to make a rich frosting."),
            ("CookTool6", "Grill the chicken for 10 minutes on each side."),
            ("CookTool7", "Stir frequently to avoid burning."),
            ("CookTool8", "Serve the dish with a sprinkle of parsley."),
        };

        var embeddingService = new FakeEmbeddingService();
        var indexService = new McpFunctionIndexService(new DummyMcpClientService(), embeddingService, NullLogger<McpFunctionIndexService>.Instance);

        // Populate the internal index with embeddings for the lines above
        var indexField = typeof(McpFunctionIndexService).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, float[]>)indexField.GetValue(indexService)!;
        foreach (var (name, text) in lines)
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(text, "model");
            dict[$"srv:{name}"] = embedding;
        }

        var result = await indexService.SelectRelevantFunctionsAsync("How does a transistor work?", 2);

        Assert.Equal(2, result.Count);
        Assert.Contains("TransTool1", result);
        Assert.Contains("TransTool2", result);
    }
}
