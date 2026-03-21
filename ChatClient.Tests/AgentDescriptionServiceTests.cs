using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

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
    public async Task GetAllAsync_AssignsBindingIdsToExistingBindingsWithoutIds()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
[
  {
    "Id": "24d79938-c3a3-44ae-86ed-22e4f43d9c35",
    "AgentName": "Test",
    "Content": "Test content",
    "ModelName": "test-model",
    "LlmId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "FunctionSettings": {
      "AutoSelectCount": 3
    },
    "McpServerBindings": [
      {
        "ServerName": "srv",
        "Enabled": true,
        "SelectAllTools": false,
        "SelectedTools": [
          "fn1",
          "fn2"
        ],
        "Roots": [],
        "Parameters": {}
      }
    ]
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
            var retrieved = await service.GetByIdAsync(Guid.Parse("24d79938-c3a3-44ae-86ed-22e4f43d9c35"));

            Assert.NotNull(retrieved);
            Assert.Equal("test-model", retrieved!.ModelName);
            Assert.Equal(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"), retrieved.LlmId);
            Assert.Equal(3, retrieved.FunctionSettings.AutoSelectCount);
            var binding = Assert.Single(retrieved.McpServerBindings);
            Assert.NotNull(binding.BindingId);
            Assert.NotEqual(Guid.Empty, binding.BindingId);
            Assert.Equal("srv", binding.ServerName);
            Assert.False(binding.SelectAllTools);
            Assert.Equal(["fn1", "fn2"], binding.SelectedTools);

            var persistedJson = await File.ReadAllTextAsync(tempFile);
            Assert.Contains("\"BindingId\"", persistedJson);
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

    [Fact]
    public async Task GetAllAsync_AssignsBindingIdsAcrossMultipleBindings()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
[
  {
    "Id": "24d79938-c3a3-44ae-86ed-22e4f43d9c35",
    "AgentName": "C# Code Assistant",
    "Content": "Test content",
    "FunctionSettings": {
      "AutoSelectCount": 10
    },
    "McpServerBindings": [
      {
        "ServerId": "da46c3f1-6bc6-4f0b-bd7b-6176daf6f6d8",
        "ServerName": "Built-in Knowledge Book MCP Server",
        "DisplayName": "Notebook",
        "Enabled": true,
        "SelectAllTools": true,
        "SelectedTools": [],
        "Roots": [],
        "Parameters": {
          "knowledgeFile": "CSharp.md"
        }
      },
      {
        "ServerName": "NugetMcpServer",
        "Enabled": true,
        "SelectAllTools": false,
        "SelectedTools": [
          "SearchPackages",
          "GetClassDefinition"
        ],
        "Roots": [],
        "Parameters": {}
      }
    ]
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

            var agent = Assert.Single(agents);
            Assert.Equal(2, agent.McpServerBindings.Count);
            Assert.All(agent.McpServerBindings, static binding =>
            {
                Assert.NotNull(binding.BindingId);
                Assert.NotEqual(Guid.Empty, binding.BindingId);
            });

            var knowledgeBinding = Assert.Single(
                agent.McpServerBindings,
                binding => string.Equals(binding.ServerName, "Built-in Knowledge Book MCP Server", StringComparison.OrdinalIgnoreCase));
            Assert.True(knowledgeBinding.SelectAllTools);

            var nugetBinding = Assert.Single(
                agent.McpServerBindings,
                binding => string.Equals(binding.ServerName, "NugetMcpServer", StringComparison.OrdinalIgnoreCase));
            Assert.False(nugetBinding.SelectAllTools);
            Assert.Equal(["SearchPackages", "GetClassDefinition"], nugetBinding.SelectedTools);

            var repoReloadedLogger = new LoggerFactory().CreateLogger<AgentDescriptionRepository>();
            var repositoryReloaded = new AgentDescriptionRepository(config, repoReloadedLogger);
            var persistedAgent = Assert.Single(await repositoryReloaded.GetAllAsync());
            Assert.Equal(2, persistedAgent.McpServerBindings.Count);

            var persistedJson = await File.ReadAllTextAsync(tempFile);
            Assert.Equal(2, Regex.Matches(persistedJson, "\"BindingId\"").Count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
