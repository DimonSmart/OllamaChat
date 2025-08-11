using System.Collections.Concurrent;
using System.Reflection;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;

namespace ChatClient.Tests;

public class McpFunctionIndexServiceRealOllamaTests
{
    private sealed class DummyMcpClientService : IMcpClientService
    {
        public Task<IReadOnlyCollection<IMcpClient>> GetMcpClientsAsync()
            => Task.FromResult<IReadOnlyCollection<IMcpClient>>([]);
        public Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient)
            => Task.FromResult<IReadOnlyList<McpClientTool>>([]);
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
            ("CookTool1", "To bake a cake, start by preheating your oven."),
            ("CookTool2", "This recipe uses fresh tomatoes and basil."),
            ("CookTool3", "Heat oil in a pan and add chopped onions."),
            ("CookTool4", "Add the pasta and cook until al dente."),
            ("CookTool5", "Mix chocolate with butter to make a rich frosting."),
            ("CookTool6", "Grill the chicken for 10 minutes on each side."),
            ("CookTool7", "Stir frequently to avoid burning."),
            ("CookTool8", "Serve the dish with a sprinkle of parsley."),
            ("TransTool2", "A transistor can act as a switch in electronic circuits."),
        };

        var configuration = new ConfigurationBuilder().Build();
        var userSettings = new DummyUserSettingsService();

        // Set up service provider for dependencies
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        var serviceProvider = services.BuildServiceProvider();

        using var embeddingService = new OllamaService(
            configuration,
            userSettings,
            NullLogger<OllamaService>.Instance,
            serviceProvider);

        var indexService = new McpFunctionIndexService(new DummyMcpClientService(), embeddingService, configuration, userSettings, NullLogger<McpFunctionIndexService>.Instance);

        var indexField = typeof(McpFunctionIndexService).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (ConcurrentDictionary<string, float[]>)indexField.GetValue(indexService)!;
        foreach (var (name, text) in lines)
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(text, "nomic-embed-text");
            dict[$"srv:{name}"] = embedding;
        }

        var result = await indexService.SelectRelevantFunctionsAsync("How does a transistor work?", 2);

        Assert.Equal(2, result.Count);
        Assert.Contains("srv:TransTool1", result);
        Assert.Contains("srv:TransTool2", result);
    }
}
