using ChatClient.Api.Repositories;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace ChatClient.Tests;

public class UserSettingsServiceTests
{
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

    [Fact]
    public async Task SaveAndLoadSettings_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "user_settings.json");

        try
        {
            var repositoryLogger = new LoggerFactory().CreateLogger<JsonFileRepository<UserSettings>>();
            var repository = new JsonFileRepository<UserSettings>(filePath, repositoryLogger);
            var serviceLogger = new LoggerFactory().CreateLogger<UserSettingsService>();
            var mockLlmService = new MockLlmServerConfigService();
            var service = new UserSettingsService(repository, serviceLogger, mockLlmService);

            var serverId = Guid.NewGuid();
            var testSettings = new UserSettings
            {
                DefaultModel = new(serverId, "test-model"),
                UserName = "Test User"
            };

            await service.SaveSettingsAsync(testSettings);
            var loadedSettings = await service.GetSettingsAsync();

            Assert.Equal(testSettings.DefaultModel.ModelName, loadedSettings.DefaultModel.ModelName);
            Assert.Equal(testSettings.DefaultModel.ServerId, loadedSettings.DefaultModel.ServerId);
            Assert.Equal(testSettings.UserName, loadedSettings.UserName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
