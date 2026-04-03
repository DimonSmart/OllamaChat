using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionSeederTests
{
    [Fact]
    public async Task SeedAsync_AddsSeededWorkflows_WhenRepositoryAlreadyContainsUserWorkflows()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "workflow-seeder-tests", Guid.NewGuid().ToString("N")));

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = root.FullName
                })
                .Build();

            await CreateSeedWorkflowSourcesAsync(root.FullName);

            var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug));
            IWorkflowDefinitionRepository repository = new WorkflowDefinitionRepository(
                configuration,
                loggerFactory.CreateLogger<WorkflowDefinitionRepository>());

            await repository.SaveAllAsync(
            [
                new SavedWorkflowDefinition
                {
                    Id = Guid.NewGuid(),
                    Kind = WorkflowDefinitionKinds.Handoff,
                    WorkflowId = "user-defined-workflow",
                    DisplayName = "User Workflow",
                    Description = "Existing custom workflow.",
                    SourceCode = "var workflow = 42;",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]);

            var seeder = new WorkflowDefinitionSeeder(
                repository,
                new WorkflowDefinitionCompiler(),
                configuration,
                new StubHostEnvironment(root.FullName),
                loggerFactory.CreateLogger<WorkflowDefinitionSeeder>());

            await seeder.SeedAsync();

            var all = await repository.GetAllAsync();

            Assert.Contains(all, workflow => string.Equals(
                workflow.WorkflowId,
                "user-defined-workflow",
                StringComparison.OrdinalIgnoreCase));

            Assert.Contains(all, workflow => string.Equals(workflow.WorkflowId, "seeded-handoff", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(all, workflow => string.Equals(workflow.WorkflowId, "seeded-sequential", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task CreateSeedWorkflowSourcesAsync(string rootPath)
    {
        var workflowDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "Data", "workflows"));

        await File.WriteAllTextAsync(
            Path.Combine(workflowDirectory.FullName, "seeded-handoff.workflow.csx"),
            """
            var workflow = WorkflowDefinitionBuilder
                .New("seeded-handoff", "Seeded Handoff")
                .Agent("triage", agent => agent
                    .Role("Router")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Seeded Triage", "triage")
                            .WithInstructions("Route the request.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .UseHandoff(handoff => handoff
                    .StartWith("triage"))
                .Build();

            workflow
            """);

        await File.WriteAllTextAsync(
            Path.Combine(workflowDirectory.FullName, "seeded-sequential.workflow.csx"),
            """
            var workflow = WorkflowDefinitionBuilder
                .New("seeded-sequential", "Seeded Sequential")
                .Agent("first", agent => agent
                    .Role("First")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Seeded First", "first")
                            .WithInstructions("Do the first step.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Agent("second", agent => agent
                    .Role("Second")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Seeded Second", "second")
                            .WithInstructions("Do the second step.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .UseSequential(sequential => sequential
                    .Order("first", "second"))
                .Build();

            workflow
            """);
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ChatClient.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
