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
    public async Task MigrationSavesSettingsAndRemovesLegacyFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "user_settings.json");
        var legacy = """
        {
            "version":1,
            "ollamaServerUrl":"http://localhost:11434",
            "ollamaBasicAuthPassword":"secret",
            "ignoreSslErrors":true,
            "httpTimeoutSeconds":10
        }
        """;
        await File.WriteAllTextAsync(filePath, legacy);
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

            var settings = await service.GetSettingsAsync();

            var json = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("ollamaServerUrl", out _));
            Assert.False(root.TryGetProperty("ollamaBasicAuthPassword", out _));
            Assert.False(root.TryGetProperty("ignoreSslErrors", out _));
            Assert.False(root.TryGetProperty("httpTimeoutSeconds", out _));
            Assert.NotEmpty(settings.Llms);
            Assert.Equal(2, settings.Version);
            Assert.NotNull(settings.DefaultLlmId);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
