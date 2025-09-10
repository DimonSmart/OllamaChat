using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;

public class OllamaServiceTests
{
    [Fact]
    public async Task GetClientAsync_ThrowsException_WhenServerNotFound()
    {
        var userSettingsService = new StubUserSettingsService();
        var llmServerConfigService = new MockLlmServerConfigService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<HttpLoggingHandler>();
        services.AddHttpClient();
        services.AddHttpClient("ollama");
        services.AddHttpClient("ollama-insecure")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OllamaService>>();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var service = new OllamaService(userSettingsService, llmServerConfigService, factory, logger);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetClientAsync(Guid.Empty));
        Assert.Contains("not found", exception.Message);
    }

    private sealed class StubUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserSettings());

        public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task GetClientAsync_RetriesAfterFailure_WhenServerIsAdded()
    {
        var serverId = Guid.NewGuid();
        var userSettingsService = new StubUserSettingsService();
        var llmServerConfigService = new MockLlmServerConfigService();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<HttpLoggingHandler>();
        services.AddHttpClient();
        services.AddHttpClient("ollama");
        services.AddHttpClient("ollama-insecure")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OllamaService>>();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var service = new OllamaService(userSettingsService, llmServerConfigService, factory, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetClientAsync(serverId));

        llmServerConfigService.Add(new LlmServerConfig { Id = serverId, ServerType = ServerType.Ollama });

        var client = await service.GetClientAsync(serverId);
        Assert.NotNull(client);
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        private readonly List<LlmServerConfig> _servers = [];

        public void Add(LlmServerConfig server)
            => _servers.Add(server);

        public Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync()
            => Task.FromResult<IReadOnlyCollection<LlmServerConfig>>(_servers);

        public Task<LlmServerConfig?> GetByIdAsync(Guid id)
            => Task.FromResult(_servers.FirstOrDefault(s => s.Id == id));

        public Task CreateAsync(LlmServerConfig serverConfig)
            => Task.CompletedTask;

        public Task UpdateAsync(LlmServerConfig serverConfig)
            => Task.CompletedTask;

        public Task DeleteAsync(Guid id)
            => Task.CompletedTask;
    }
}
