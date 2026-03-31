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
    public async Task SeedAsync_AddsStarterWorkflows_WhenRepositoryAlreadyContainsUserWorkflows()
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

            foreach (var template in WorkflowCodeTemplates.StarterTemplates)
            {
                Assert.Contains(all, workflow => string.Equals(
                    workflow.WorkflowId,
                    template.WorkflowId,
                    StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ChatClient.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
