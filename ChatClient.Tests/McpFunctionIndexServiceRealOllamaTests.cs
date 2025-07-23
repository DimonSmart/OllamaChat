using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ChatClient.Tests;

public class McpFunctionIndexServiceRealOllamaTests
{
    private sealed class DummyMcpClientService : IMcpClientService
    {
        public Task<IReadOnlyCollection<ModelContextProtocol.Client.IMcpClient>> GetMcpClientsAsync()
            => Task.FromResult<IReadOnlyCollection<ModelContextProtocol.Client.IMcpClient>>(Array.Empty<ModelContextProtocol.Client.IMcpClient>());
        public Task<IReadOnlyList<McpClientTool>> GetMcpTools(ModelContextProtocol.Client.IMcpClient mcpClient)
            => Task.FromResult<IReadOnlyList<McpClientTool>>(Array.Empty<McpClientTool>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync() => Task.FromResult(new UserSettings());
        public Task SaveSettingsAsync(UserSettings settings) => Task.CompletedTask;
    }

    [Fact(Skip = "Requires running Ollama server with 'nomic-embed-text' model. Run manually.")]
    public async Task SelectRelevantFunctions_WithRealOllama_FindsTransistorEntries()
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

        var configuration = new ConfigurationBuilder().Build();
        using var embeddingService = new OllamaService(configuration, new DummyUserSettingsService());
        var indexService = new McpFunctionIndexService(new DummyMcpClientService(), embeddingService, NullLogger<McpFunctionIndexService>.Instance);

        // Populate the internal index with embeddings for the lines above
        var indexField = typeof(McpFunctionIndexService).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, float[]>)indexField.GetValue(indexService)!;
        foreach (var (name, text) in lines)
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(text, "nomic-embed-text");
            dict[$"srv:{name}"] = embedding;
        }

        var result = await indexService.SelectRelevantFunctionsAsync("How does a transistor work?", 2);

        Assert.Equal(2, result.Count);
        Assert.Contains("TransTool1", result);
        Assert.Contains("TransTool2", result);
    }
}
