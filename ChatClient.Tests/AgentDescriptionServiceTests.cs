using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class AgentDescriptionServiceTests
{
    [Fact]
    public async Task CreatePrompt_PersistsModelNameFunctionsAndAutoSelectCount()
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

            var serverId = Guid.NewGuid();
            var prompt = new AgentDescription
            {
                AgentName = "Test",
                Content = "Test content",
                ModelName = "test-model",
                LlmId = serverId,
                FunctionSettings = new FunctionSettings
                {
                    SelectedFunctions = ["srv:fn1", "srv:fn2"],
                    AutoSelectCount = 3
                }
            };

            var created = await service.CreateAsync(prompt);

            var serviceReloaded = new AgentDescriptionService(config, logger);
            var retrieved = await serviceReloaded.GetByIdAsync(created.Id);

            Assert.NotNull(retrieved);
            Assert.Equal("test-model", retrieved!.ModelName);
            Assert.Equal(serverId, retrieved.LlmId);
            Assert.Equal(["srv:fn1", "srv:fn2"], retrieved.FunctionSettings.SelectedFunctions);
            Assert.Equal(3, retrieved.FunctionSettings.AutoSelectCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
