using ChatClient.Api.Services;
using ChatClient.Infrastructure.Repositories;
using ChatClient.Domain.Models;
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

            var repoLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repository = new AgentDescriptionRepository(config, repoLogger);
            var service = new AgentDescriptionService(repository);

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

            await service.CreateAsync(prompt);

            var repoReloadedLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repositoryReloaded = new AgentDescriptionRepository(config, repoReloadedLogger);
            var serviceReloaded = new AgentDescriptionService(repositoryReloaded);
            var retrieved = await serviceReloaded.GetByIdAsync(prompt.Id);

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
