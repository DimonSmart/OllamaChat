using System;
using System.IO;
using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class UserSettingsServiceTests
{
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
                    ["UserSettings:Directory"] = tempDir
                })
                .Build();
            var logger = new LoggerFactory().CreateLogger<UserSettingsService>();
            var service = new UserSettingsService(config, logger);

            var serverId = Guid.NewGuid();
            var testSettings = new UserSettings
            {
                DefaultModelName = "test-model",
                DefaultLlmId = serverId,
                UserName = "Test User"
            };

            await service.SaveSettingsAsync(testSettings);
            var loadedSettings = await service.GetSettingsAsync();

            Assert.Equal(testSettings.DefaultModelName, loadedSettings.DefaultModelName);
            Assert.Equal(testSettings.DefaultLlmId, loadedSettings.DefaultLlmId);
            Assert.Equal(testSettings.UserName, loadedSettings.UserName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
