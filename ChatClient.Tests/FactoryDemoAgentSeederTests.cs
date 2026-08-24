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

public sealed class FactoryDemoAgentSeederTests
{
    private static readonly Guid FactoryDemoCoordinatorId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900101");
    private static readonly Guid FactoryPlannerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900102");
    private static readonly Guid FactoryWorkerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900103");
    private static readonly Guid FactoryReviewerId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900104");
    private static readonly Guid FactoryDemoFileAccessProfileId = Guid.Parse("9a20d8c1-3c32-4e75-9f00-0d7f5b900201");

    [Fact]
    public async Task SeedAsync_AddsFactoryDemoTopologyAndWorkspaceProfile()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "factory-demo-agent-seeder-tests", Guid.NewGuid().ToString("N")));

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root.FullName
                })
                .Build();

            await CreateMinimalSeedFileAsync(root.FullName);

            using var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug));
            IAgentTemplateRepository templateRepository = new AgentTemplateRepository(
                configuration,
                loggerFactory.CreateLogger<AgentTemplateRepository>());
            IFileAccessProviderProfileRepository fileAccessRepository = new FileAccessProviderProfileRepository(
                configuration,
                loggerFactory.CreateLogger<FileAccessProviderProfileRepository>());

            var seeder = new AgentTemplateSeeder(
                templateRepository,
                configuration,
                new StubHostEnvironment(root.FullName),
                loggerFactory.CreateLogger<AgentTemplateSeeder>(),
                fileAccessRepository);

            await seeder.SeedAsync();

            var templates = (await templateRepository.GetAllAsync(TestContext.Current.CancellationToken)).ToList();
            var coordinator = Assert.Single(templates, template => template.Id == FactoryDemoCoordinatorId);
            var planner = Assert.Single(templates, template => template.Id == FactoryPlannerId);
            var worker = Assert.Single(templates, template => template.Id == FactoryWorkerId);
            var reviewer = Assert.Single(templates, template => template.Id == FactoryReviewerId);

            Assert.Equal("Factory Demo", coordinator.AgentName);
            Assert.Equal(new[] { FactoryPlannerId, FactoryWorkerId, FactoryReviewerId }, coordinator.BackgroundAgentIds);
            Assert.Empty(planner.BackgroundAgentIds);
            Assert.Empty(worker.BackgroundAgentIds);
            Assert.Empty(reviewer.BackgroundAgentIds);

            Assert.False(coordinator.EnableShell);
            Assert.False(planner.EnableShell);
            Assert.True(worker.EnableShell);
            Assert.True(reviewer.EnableShell);
            Assert.Contains("NEEDS_FIX` and `NEEDS_REPLAN` are intermediate control-flow states", coordinator.Content);
            Assert.Contains("Do not stop at a narration that you will replan", coordinator.Content);
            Assert.Contains("Close out the durable run record before sending the final user response", coordinator.Content);
            Assert.Contains("set the phase to `completed`", coordinator.Content);
            Assert.Contains("Never replace the event history with only the newest event", coordinator.Content);
            Assert.Contains("you must invoke `Factory Reviewer` yourself", coordinator.Content);
            Assert.Contains("Accept `APPROVED` only from the result of that Reviewer invocation", coordinator.Content);
            Assert.Contains("Do not create a Worker task whose outcome is the final review", planner.Content);
            Assert.Contains("Shell commands run in a Linux container rooted at `/workspace`", worker.Content);
            Assert.Contains("never use PowerShell commands or backslash paths", worker.Content);
            Assert.Contains("Do not issue `$null`, `true`, `echo`, or any other no-op", worker.Content);
            Assert.Contains("creates `Name.slnx` by default", worker.Content);
            Assert.Contains("every required `ProjectReference` must exist", worker.Content);
            Assert.Contains("Use fail-fast command composition", worker.Content);
            Assert.Contains("Never create, replace, or approve any file under `.factory-demo/reviews/`", worker.Content);

            foreach (var template in new[] { coordinator, planner, worker, reviewer })
            {
                Assert.Equal(FactoryDemoFileAccessProfileId, template.FileAccessProviderProfileId);
                Assert.False(template.EnableFileMemory);
            }

            var profiles = await fileAccessRepository.GetAllAsync(TestContext.Current.CancellationToken);
            var profile = Assert.Single(profiles, item => item.Id == FactoryDemoFileAccessProfileId);
            Assert.Equal("Factory Demo Workspace", profile.Name);
            Assert.Contains("File Access writes replace a file", profile.Instructions);
            Assert.Equal(FileAccessMode.ReadWrite, profile.AccessMode);
            Assert.False(profile.RequireReadApproval);
            Assert.False(profile.RequireWriteApproval);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task CreateMinimalSeedFileAsync(string rootPath)
    {
        var dataDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "Data"));
        var seedPath = Path.Combine(dataDirectory.FullName, "agent_templates.json");
        var seeded = new[]
        {
            new AgentTemplateDefinition
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                AgentName = "Seed Agent",
                Summary = "Seed fixture.",
                Content = "Be helpful."
            }
        };

        await File.WriteAllTextAsync(
            seedPath,
            JsonSerializer.Serialize(seeded, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }),
            TestContext.Current.CancellationToken);
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ChatClient.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
