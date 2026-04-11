using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace ChatClient.Tests;

public class UserSettingsServiceTests
{
    private sealed class ThrowingUserSettingsRepository : IUserSettingsRepository
    {
        public bool Exists => true;

        public Task<UserSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UserSettings?>(new UserSettings());

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
            throw new IOException("disk write failed");
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        public Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync()
        {
            return Task.FromResult<IReadOnlyCollection<LlmServerConfig>>([]);
        }

        public Task<LlmServerConfig?> GetByIdAsync(Guid serverId)
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

        public Task DeleteAsync(Guid serverId)
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
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserSettings:FilePath"] = filePath
                })
                .Build();
            var repositoryLogger = new LoggerFactory().CreateLogger<UserSettingsRepository>();
            var repository = new UserSettingsRepository(config, repositoryLogger);
            var serviceLogger = new LoggerFactory().CreateLogger<UserSettingsService>();
            var mockLlmService = new MockLlmServerConfigService();
            var service = new UserSettingsService(repository, serviceLogger, mockLlmService);

            var serverId = Guid.NewGuid();
            var testSettings = new UserSettings
            {
                DefaultModel = new(serverId, "test-model"),
                UserName = "Test User",
                VoiceInput = new VoiceInputSettings
                {
                    IsEnabled = true,
                    Status = VoiceInputInitializationStatus.Ready,
                    RecognitionLanguage = "auto"
                }
            };

            await service.SaveSettingsAsync(testSettings);
            var loadedSettings = await service.GetSettingsAsync();

            Assert.Equal(testSettings.DefaultModel.ModelName, loadedSettings.DefaultModel.ModelName);
            Assert.Equal(testSettings.DefaultModel.ServerId, loadedSettings.DefaultModel.ServerId);
            Assert.Equal(testSettings.UserName, loadedSettings.UserName);
            Assert.True(loadedSettings.VoiceInput.IsEnabled);
            Assert.Equal(VoiceInputInitializationStatus.Ready, loadedSettings.VoiceInput.Status);
            Assert.Equal("auto", loadedSettings.VoiceInput.RecognitionLanguage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SaveSettingsAsync_PropagatesRepositoryFailures()
    {
        var serviceLogger = new LoggerFactory().CreateLogger<UserSettingsService>();
        var service = new UserSettingsService(
            new ThrowingUserSettingsRepository(),
            serviceLogger,
            new MockLlmServerConfigService());

        await Assert.ThrowsAsync<IOException>(() => service.SaveSettingsAsync(new UserSettings()));
    }

    [Fact]
    public async Task SaveVoiceInputSettingsAsync_UpdatesVoiceInputWithoutLosingOtherSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "user_settings.json");

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserSettings:FilePath"] = filePath
                })
                .Build();
            var repositoryLogger = new LoggerFactory().CreateLogger<UserSettingsRepository>();
            var repository = new UserSettingsRepository(config, repositoryLogger);
            var serviceLogger = new LoggerFactory().CreateLogger<UserSettingsService>();
            var service = new UserSettingsService(repository, serviceLogger, new MockLlmServerConfigService());

            await service.SaveSettingsAsync(new UserSettings
            {
                UserName = "Test User",
                VoiceInput = new VoiceInputSettings
                {
                    IsEnabled = false,
                    Status = VoiceInputInitializationStatus.NotInitialized,
                    RecognitionLanguage = "auto"
                }
            });

            await service.SaveVoiceInputSettingsAsync(new VoiceInputSettings
            {
                IsEnabled = true,
                Status = VoiceInputInitializationStatus.Ready,
                RecognitionLanguage = "auto"
            });

            var loadedSettings = await service.GetSettingsAsync();

            Assert.Equal("Test User", loadedSettings.UserName);
            Assert.True(loadedSettings.VoiceInput.IsEnabled);
            Assert.Equal(VoiceInputInitializationStatus.Ready, loadedSettings.VoiceInput.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
