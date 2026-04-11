using ChatClient.Api.VoiceInput;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatClient.Tests;

public class VoiceInputServiceTests
{
    private sealed class TestUserSettingsService : IUserSettingsService
    {
        public UserSettings Settings { get; set; } = new();

        public int SaveCount { get; private set; }

        public Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GetSettingsAsync_DowngradesReadyState_WhenModelFileIsMissing()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var userSettingsService = new TestUserSettingsService
            {
                Settings = new UserSettings
                {
                    VoiceInput = new VoiceInputSettings
                    {
                        Status = VoiceInputInitializationStatus.Ready
                    }
                }
            };

            using var service = CreateService(tempDirectory, userSettingsService);
            var settings = await service.GetSettingsAsync();

            Assert.Equal(VoiceInputInitializationStatus.NotInitialized, settings.Status);
            Assert.Equal(1, userSettingsService.SaveCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetSettingsAsync_KeepsReadyState_WhenModelFileExists()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDirectory, "ggml-base.bin"), "model");

            var userSettingsService = new TestUserSettingsService
            {
                Settings = new UserSettings
                {
                    VoiceInput = new VoiceInputSettings
                    {
                        Status = VoiceInputInitializationStatus.Ready
                    }
                }
            };

            using var service = CreateService(tempDirectory, userSettingsService);
            var settings = await service.GetSettingsAsync();

            Assert.Equal(VoiceInputInitializationStatus.Ready, settings.Status);
            Assert.Equal(0, userSettingsService.SaveCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static VoiceInputService CreateService(string tempDirectory, IUserSettingsService userSettingsService)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VoiceInput:DirectoryPath"] = tempDirectory
            })
            .Build();

        return new VoiceInputService(
            configuration,
            Options.Create(new VoiceInputOptions
            {
                DirectoryPath = tempDirectory,
                ModelType = "Base",
                RecognitionLanguage = "auto"
            }),
            userSettingsService,
            NullLogger<VoiceInputService>.Instance);
    }
}
