using ChatClient.Api.Services.Seed;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class AgentTemplateSeederTests
{
    private static readonly Guid SeededAssistantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeededPhilosopherId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task SeedAsync_AddsSeededTemplates_WhenRepositoryAlreadyContainsUserTemplates()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "agent-template-seeder-tests", Guid.NewGuid().ToString("N")));

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root.FullName
                })
                .Build();

            await CreateSeedAgentTemplatesAsync(root.FullName);

            var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug));
            IAgentTemplateRepository repository = new AgentTemplateRepository(
                configuration,
                loggerFactory.CreateLogger<AgentTemplateRepository>());

            await repository.SaveAllAsync(
            [
                new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "User Agent",
                    Summary = "User-defined agent.",
                    Content = "Custom content.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]);

            var seeder = new AgentTemplateSeeder(
                repository,
                configuration,
                new StubHostEnvironment(root.FullName),
                loggerFactory.CreateLogger<AgentTemplateSeeder>());

            await seeder.SeedAsync();

            var all = await repository.GetAllAsync();

            Assert.Contains(all, agent => string.Equals(agent.AgentName, "User Agent", StringComparison.Ordinal));
            Assert.Contains(all, agent => agent.Id == SeededAssistantId);
            Assert.Contains(all, agent => agent.Id == SeededPhilosopherId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SeedAsync_DoesNotOverwriteExistingSeededTemplate()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "agent-template-seeder-tests", Guid.NewGuid().ToString("N")));

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root.FullName
                })
                .Build();

            await CreateSeedAgentTemplatesAsync(root.FullName);

            var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug));
            IAgentTemplateRepository repository = new AgentTemplateRepository(
                configuration,
                loggerFactory.CreateLogger<AgentTemplateRepository>());

            var createdAt = DateTime.UtcNow.AddDays(-2);
            await repository.SaveAllAsync(
            [
                new AgentTemplateDefinition
                {
                    Id = SeededPhilosopherId,
                    AgentName = "Immanuel Kant",
                    Summary = "Locally edited seeded template.",
                    Content = "Local override.",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                }
            ]);

            var seeder = new AgentTemplateSeeder(
                repository,
                configuration,
                new StubHostEnvironment(root.FullName),
                loggerFactory.CreateLogger<AgentTemplateSeeder>());

            await seeder.SeedAsync();

            var seeded = Assert.Single(
                await repository.GetAllAsync(),
                agent => agent.Id == SeededPhilosopherId);

            Assert.Equal(createdAt, seeded.CreatedAt);
            Assert.Equal("Local override.", seeded.Content);
            Assert.Equal("Locally edited seeded template.", seeded.Summary);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RestoreSeededAsync_RefreshesSeededTemplatesWithoutRemovingUserTemplates()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "agent-template-seeder-tests", Guid.NewGuid().ToString("N")));

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root.FullName
                })
                .Build();

            await CreateSeedAgentTemplatesAsync(root.FullName);

            var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug));
            IAgentTemplateRepository repository = new AgentTemplateRepository(
                configuration,
                loggerFactory.CreateLogger<AgentTemplateRepository>());

            var createdAt = DateTime.UtcNow.AddDays(-3);
            var customId = Guid.NewGuid();
            await repository.SaveAllAsync(
            [
                new AgentTemplateDefinition
                {
                    Id = SeededPhilosopherId,
                    AgentName = "Immanuel Kant",
                    Summary = "Locally edited seeded template.",
                    Content = "Local override.",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                },
                new AgentTemplateDefinition
                {
                    Id = customId,
                    AgentName = "User Agent",
                    Summary = "Custom template.",
                    Content = "Custom content.",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                }
            ]);

            var seeder = new AgentTemplateSeeder(
                repository,
                configuration,
                new StubHostEnvironment(root.FullName),
                loggerFactory.CreateLogger<AgentTemplateSeeder>());

            await seeder.RestoreSeededAsync();

            var all = (await repository.GetAllAsync()).ToList();
            var restored = Assert.Single(all, agent => agent.Id == SeededPhilosopherId);
            var custom = Assert.Single(all, agent => agent.Id == customId);

            Assert.Equal(createdAt, restored.CreatedAt);
            Assert.Equal("Seeded philosopher.", restored.Summary);
            Assert.Equal("Defend reason and duty.", restored.Content);
            Assert.Equal("User Agent", custom.AgentName);
            Assert.Equal("Custom content.", custom.Content);
            Assert.Contains(all, agent => agent.Id == SeededAssistantId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task CreateSeedAgentTemplatesAsync(string rootPath)
    {
        var dataDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "Data"));
        var seedPath = Path.Combine(dataDirectory.FullName, "agent_templates.json");

        var seeded = new List<AgentTemplateDefinition>
        {
            new()
            {
                Id = SeededAssistantId,
                AgentName = "Default Assistant",
                Summary = "Seeded default assistant.",
                Content = "Be helpful.",
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                UpdatedAt = DateTime.UtcNow.AddDays(-10)
            },
            new()
            {
                Id = SeededPhilosopherId,
                AgentName = "Immanuel Kant",
                Summary = "Seeded philosopher.",
                Content = "Defend reason and duty.",
                CreatedAt = DateTime.UtcNow.AddDays(-9),
                UpdatedAt = DateTime.UtcNow.AddDays(-9)
            }
        };

        await File.WriteAllTextAsync(
            seedPath,
            JsonSerializer.Serialize(seeded, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ChatClient.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
