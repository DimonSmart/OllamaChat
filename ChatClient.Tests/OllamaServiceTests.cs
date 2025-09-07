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
    public async Task GetClientAsync_UsesDefaultUrl_WhenNoServersConfigured()
    {
        var userSettingsService = new StubUserSettingsService();
        var llmServerConfigService = new MockLlmServerConfigService();
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OllamaService>>();

        var service = new OllamaService(userSettingsService, llmServerConfigService, provider, logger);

        await service.GetClientAsync(Guid.Empty);

        var field = typeof(OllamaService).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<Guid, (OllamaApiClient Client, HttpClient HttpClient)>)field.GetValue(service)!;
        var httpClient = dict[Guid.Empty].HttpClient;

        Assert.Equal(new Uri(LlmServerConfig.DefaultOllamaUrl), httpClient.BaseAddress);
    }

    private sealed class StubUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserSettings());

        public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        public Task<List<LlmServerConfig>> GetAllAsync()
        {
            return Task.FromResult<List<LlmServerConfig>>([]);
        }

        public Task<LlmServerConfig?> GetByIdAsync(Guid id)
        {
            return Task.FromResult<LlmServerConfig?>(null);
        }

        public Task<LlmServerConfig> CreateAsync(LlmServerConfig server)
        {
            return Task.FromResult(server);
        }

        public Task<LlmServerConfig> UpdateAsync(LlmServerConfig server)
        {
            return Task.FromResult(server);
        }

        public Task DeleteAsync(Guid id)
        {
            return Task.CompletedTask;
        }
    }
}
