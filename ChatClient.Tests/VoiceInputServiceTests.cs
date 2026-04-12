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

        public Task SaveVoiceInputSettingsAsync(VoiceInputSettings voiceInputSettings, CancellationToken cancellationToken = default)
        {
            Settings.VoiceInput = voiceInputSettings;
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
                        Status = VoiceInputInitializationStatus.Ready,
                        ModelType = "Small"
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
            await File.WriteAllTextAsync(Path.Combine(tempDirectory, "ggml-small.bin"), "model");

            var userSettingsService = new TestUserSettingsService
            {
                Settings = new UserSettings
                {
                    VoiceInput = new VoiceInputSettings
                    {
                        Status = VoiceInputInitializationStatus.Ready,
                        ModelType = "Small"
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

    [Fact]
    public async Task GetSettingsAsync_NormalizesMissingModelType_FromConfiguredDefault()
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
                        ModelType = string.Empty
                    }
                }
            };

            using var service = CreateService(tempDirectory, userSettingsService, configuredModelType: "Small");
            var settings = await service.GetSettingsAsync();

            Assert.Equal("Small", settings.ModelType);
            Assert.Equal(1, userSettingsService.SaveCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetStorageInfoAsync_ReturnsDownloadedModelsAndTotalSize()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDirectory, "ggml-small.bin"), "12345");
            await File.WriteAllTextAsync(Path.Combine(tempDirectory, "ggml-base.bin"), "123");

            var userSettingsService = new TestUserSettingsService();
            using var service = CreateService(tempDirectory, userSettingsService);

            var storageInfo = await service.GetStorageInfoAsync();

            Assert.Equal(8, storageInfo.TotalBytes);
            Assert.Collection(
                storageInfo.DownloadedModels.OrderBy(model => model.ModelType, StringComparer.Ordinal),
                model =>
                {
                    Assert.Equal("Base", model.ModelType);
                    Assert.Equal(3, model.SizeBytes);
                },
                model =>
                {
                    Assert.Equal("Small", model.ModelType);
                    Assert.Equal(5, model.SizeBytes);
                });
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ClearDownloadedModelsAsync_RemovesFilesAndResetsStatus()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var modelFilePath = Path.Combine(tempDirectory, "ggml-small.bin");
            var partialDownloadPath = Path.Combine(tempDirectory, "ggml-large-v3.bin.download");
            await File.WriteAllTextAsync(modelFilePath, "model");
            await File.WriteAllTextAsync(partialDownloadPath, "partial");

            var userSettingsService = new TestUserSettingsService
            {
                Settings = new UserSettings
                {
                    VoiceInput = new VoiceInputSettings
                    {
                        Status = VoiceInputInitializationStatus.Ready,
                        ModelType = "Small",
                        ErrorMessage = "old error"
                    }
                }
            };

            using var service = CreateService(tempDirectory, userSettingsService);
            var settings = await service.ClearDownloadedModelsAsync();

            Assert.False(File.Exists(modelFilePath));
            Assert.False(File.Exists(partialDownloadPath));
            Assert.Equal(VoiceInputInitializationStatus.NotInitialized, settings.Status);
            Assert.Equal(string.Empty, settings.ErrorMessage);
            Assert.Equal(1, userSettingsService.SaveCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static VoiceInputService CreateService(
        string tempDirectory,
        IUserSettingsService userSettingsService,
        string configuredModelType = "Base")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VoiceInput:DirectoryPath"] = tempDirectory,
                ["VoiceInput:ModelType"] = configuredModelType
            })
            .Build();

        return new VoiceInputService(
            configuration,
            Options.Create(new VoiceInputOptions
            {
                DirectoryPath = tempDirectory,
                ModelType = configuredModelType,
                RecognitionLanguage = "auto"
            }),
            userSettingsService,
            NullLogger<VoiceInputService>.Instance);
    }
}
