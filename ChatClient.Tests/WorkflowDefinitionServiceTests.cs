using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionServiceTests
{
    [Fact]
    public async Task CreateAsync_AssignsIdNormalizesAndPersistsWorkflow()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");

        try
        {
            var service = CreateService(tempFile);
            var workflow = new SavedWorkflowDefinition
            {
                Id = Guid.Empty,
                Kind = " handoff ",
                WorkflowId = " interview-demo ",
                DisplayName = " Interview Demo ",
                Description = " Example workflow ",
                SourceCode = " var workflow = 42; "
            };

            await service.CreateAsync(workflow);

            Assert.NotEqual(Guid.Empty, workflow.Id);
            Assert.Equal("handoff", workflow.Kind);
            Assert.Equal("interview-demo", workflow.WorkflowId);
            Assert.Equal("Interview Demo", workflow.DisplayName);

            var reloaded = CreateService(tempFile);
            var persisted = await reloaded.GetByIdAsync(workflow.Id);

            Assert.NotNull(persisted);
            Assert.Equal("Example workflow", persisted!.Description);
            Assert.Equal("var workflow = 42;", persisted.SourceCode);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task UpdateAsync_PreservesCreatedAtAndUpdatesStoredWorkflow()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");

        try
        {
            var service = CreateService(tempFile);
            var workflow = new SavedWorkflowDefinition
            {
                WorkflowId = "demo",
                DisplayName = "Demo",
                SourceCode = "initial"
            };

            await service.CreateAsync(workflow);
            var createdAt = workflow.CreatedAt;
            await Task.Delay(20);

            workflow.DisplayName = "Updated Demo";
            workflow.Description = "Updated description";
            workflow.SourceCode = "updated";

            await service.UpdateAsync(workflow);

            var reloaded = CreateService(tempFile);
            var persisted = await reloaded.GetByIdAsync(workflow.Id);

            Assert.NotNull(persisted);
            Assert.Equal(createdAt, persisted!.CreatedAt);
            Assert.True(persisted.UpdatedAt >= createdAt);
            Assert.Equal("Updated Demo", persisted.DisplayName);
            Assert.Equal("Updated description", persisted.Description);
            Assert.Equal("updated", persisted.SourceCode);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesWorkflowFromStore()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");

        try
        {
            var service = CreateService(tempFile);
            var workflow = new SavedWorkflowDefinition
            {
                WorkflowId = "demo",
                DisplayName = "Demo",
                SourceCode = "source"
            };

            await service.CreateAsync(workflow);
            await service.DeleteAsync(workflow.Id);

            var reloaded = CreateService(tempFile);
            var all = await reloaded.GetAllAsync();

            Assert.Empty(all);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CreateAsync_NormalizesGroupChatWorkflowKind()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");

        try
        {
            var service = CreateService(tempFile);
            var workflow = new SavedWorkflowDefinition
            {
                WorkflowId = "debate",
                Kind = " Group Chat ",
                DisplayName = "Debate Workflow",
                SourceCode = "source"
            };

            await service.CreateAsync(workflow);

            Assert.Equal(WorkflowDefinitionKinds.GroupChat, workflow.Kind);

            var reloaded = CreateService(tempFile);
            var persisted = await reloaded.GetByIdAsync(workflow.Id);

            Assert.NotNull(persisted);
            Assert.Equal(WorkflowDefinitionKinds.GroupChat, persisted!.Kind);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static WorkflowDefinitionService CreateService(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowDefinitions:FilePath"] = filePath
            })
            .Build();

        var repositoryLogger = new LoggerFactory().CreateLogger<WorkflowDefinitionRepository>();
        var repository = new WorkflowDefinitionRepository(configuration, repositoryLogger);
        return new WorkflowDefinitionService(repository);
    }
}
