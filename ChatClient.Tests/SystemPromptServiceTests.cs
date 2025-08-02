using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class SystemPromptServiceTests
{
    [Fact]
    public async Task CreatePrompt_PersistsModelName()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "[]");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SystemPrompts:FilePath"] = tempFile
                })
                .Build();

            var logger = new LoggerFactory().CreateLogger<SystemPromptService>();
            var service = new SystemPromptService(config, logger);

            var prompt = new SystemPrompt
            {
                Name = "Test",
                Content = "Test content",
                ModelName = "test-model"
            };

            var created = await service.CreatePromptAsync(prompt);

            var serviceReloaded = new SystemPromptService(config, logger);
            var retrieved = await serviceReloaded.GetPromptByIdAsync(created.Id!.Value);

            Assert.NotNull(retrieved);
            Assert.Equal("test-model", retrieved!.ModelName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
