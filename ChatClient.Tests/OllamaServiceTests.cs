using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using System.Reflection;

public class OllamaServiceTests
{
    [Fact]
    public async Task GetClientAsync_UsesDefaultUrl_WhenConfigurationEmpty()
    {
        var configValues = new Dictionary<string, string?> { ["Ollama:BaseUrl"] = "" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var userSettingsService = new StubUserSettingsService();
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OllamaService>>();

        var service = new OllamaService(configuration, userSettingsService, provider, logger);

        await service.GetClientAsync(Guid.Empty);

        var field = typeof(OllamaService).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<Guid, (OllamaApiClient Client, HttpClient HttpClient)>)field.GetValue(service)!;
        var httpClient = dict[Guid.Empty].HttpClient;

        Assert.Equal(new Uri(LlmServerConfig.DefaultOllamaUrl), httpClient.BaseAddress);
    }

    private sealed class StubUserSettingsService : IUserSettingsService
    {
        public event Func<Task>? EmbeddingModelChanged;
        public Task<UserSettings> GetSettingsAsync() => Task.FromResult(new UserSettings());
        public Task SaveSettingsAsync(UserSettings settings) => Task.CompletedTask;
    }
}
