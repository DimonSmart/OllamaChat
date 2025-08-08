using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class AgentDescriptionServiceTests
{
    [Fact]
    public async Task CreatePrompt_PersistsModelNameAndFunctions()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "[]");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentDescriptions:FilePath"] = tempFile
                })
                .Build();

            var logger = new LoggerFactory().CreateLogger<AgentDescriptionService>();
            var service = new AgentDescriptionService(config, logger);

            var prompt = new AgentDescription
            {
                Name = "Test",
                Content = "Test content",
                ModelName = "test-model",
                Functions = ["srv:fn1", "srv:fn2"],
                AutoSelectFunctions = true,
                AutoSelectCount = 3
            };

            var created = await service.CreateAsync(prompt);

            var serviceReloaded = new AgentDescriptionService(config, logger);
            var retrieved = await serviceReloaded.GetByIdAsync(created.Id!.Value);

            Assert.NotNull(retrieved);
            Assert.Equal("test-model", retrieved!.ModelName);
            Assert.Equal(["srv:fn1", "srv:fn2"], retrieved.Functions);
            Assert.True(retrieved.AutoSelectFunctions);
            Assert.Equal(3, retrieved.AutoSelectCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
