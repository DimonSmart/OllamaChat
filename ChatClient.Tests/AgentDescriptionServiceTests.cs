using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class AgentDescriptionServiceTests
{
    [Fact]
    public async Task CreatePrompt_AssignsUniqueIdWhenCallerSendsEmptyGuid()
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

            var prompt = new AgentDescription
            {
                Id = Guid.Empty,
                AgentName = "Browser interactor",
                Content = "Use the browser tools."
            };

            await service.CreateAsync(prompt);

            Assert.NotEqual(Guid.Empty, prompt.Id);

            var repoReloadedLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repositoryReloaded = new AgentDescriptionRepository(config, repoReloadedLogger);
            var serviceReloaded = new AgentDescriptionService(repositoryReloaded);
            var retrieved = await serviceReloaded.GetByIdAsync(prompt.Id);

            Assert.NotNull(retrieved);
            Assert.Equal("Browser interactor", retrieved!.AgentName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

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

    [Fact]
    public async Task CreatePrompt_PersistsMcpServerBindings()
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
            var bindingId = Guid.NewGuid();
            var serverId = Guid.NewGuid();

            var prompt = new AgentDescription
            {
                AgentName = "Test MCP",
                Content = "Test content",
                McpServerBindings =
                [
                    new McpServerSessionBinding
                    {
                        BindingId = bindingId,
                        ServerId = serverId,
                        ServerName = "Built-in Knowledge Book MCP Server",
                        DisplayName = "Docs",
                        Enabled = true,
                        SelectAllTools = false,
                        SelectedTools = ["kb_search_sections"],
                        Roots = ["C:\\workspace"],
                        Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["knowledgeFile"] = "C:\\kb\\csharp.json"
                        }
                    }
                ]
            };

            await service.CreateAsync(prompt);

            var repoReloadedLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repositoryReloaded = new AgentDescriptionRepository(config, repoReloadedLogger);
            var serviceReloaded = new AgentDescriptionService(repositoryReloaded);
            var retrieved = await serviceReloaded.GetByIdAsync(prompt.Id);

            var binding = Assert.Single(retrieved!.McpServerBindings);
            Assert.Equal(bindingId, binding.BindingId);
            Assert.Equal(serverId, binding.ServerId);
            Assert.Equal("Docs", binding.DisplayName);
            Assert.False(binding.SelectAllTools);
            Assert.Equal(["kb_search_sections"], binding.SelectedTools);
            Assert.Equal("C:\\kb\\csharp.json", binding.Parameters["knowledgeFile"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetAllAsync_RepairsEmptyAndDuplicateIds()
    {
        var tempFile = Path.GetTempFileName();
        var duplicateId = Guid.NewGuid();
        File.WriteAllText(tempFile, $$"""
[
  {
    "Id": "00000000-0000-0000-0000-000000000000",
    "AgentName": "Browser interactor",
    "Content": "First"
  },
  {
    "Id": "{{duplicateId}}",
    "AgentName": "Browser commander",
    "Content": "Second"
  },
  {
    "Id": "{{duplicateId}}",
    "AgentName": "Browser commander copy",
    "Content": "Third"
  }
]
""");

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

            var agents = (await service.GetAllAsync()).ToList();

            Assert.Equal(3, agents.Count);
            Assert.DoesNotContain(agents, static agent => agent.Id == Guid.Empty);
            Assert.Equal(3, agents.Select(static agent => agent.Id).Distinct().Count());

            var repoReloadedLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repositoryReloaded = new AgentDescriptionRepository(config, repoReloadedLogger);
            var persistedAgents = (await repositoryReloaded.GetAllAsync()).ToList();

            Assert.DoesNotContain(persistedAgents, static agent => agent.Id == Guid.Empty);
            Assert.Equal(3, persistedAgents.Select(static agent => agent.Id).Distinct().Count());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
