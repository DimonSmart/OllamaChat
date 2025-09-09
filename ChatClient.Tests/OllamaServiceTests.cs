using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class OllamaServiceTests
{
    [Fact]
    public async Task GetClientAsync_ThrowsException_WhenServerNotFound()
    {
        var userSettingsService = new StubUserSettingsService();
        var llmServerConfigService = new MockLlmServerConfigService();
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<OllamaService>>();

        var service = new OllamaService(userSettingsService, llmServerConfigService, provider, logger);

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

        public Task CreateAsync(LlmServerConfig serverConfig)
        {
            return Task.CompletedTask;
        }

        public Task UpdateAsync(LlmServerConfig serverConfig)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id)
        {
            return Task.CompletedTask;
        }
    }
}
