using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgentRuntimeContractTests
{
    [Fact]
    public async Task Catalog_ReturnsSavedAgentsAndWorkflowsWithKindedReferences()
    {
        var sharedId = Guid.NewGuid();
        var agent = new AgentTemplateDefinition
        {
            Id = sharedId,
            AgentName = "Writer",
            Summary = "Writes drafts"
        };
        var workflow = new SavedWorkflowDefinition
        {
            Id = sharedId,
            DisplayName = "Review Flow",
            Description = "Reviews drafts"
        };
        var catalog = new AgentDefinitionCatalog(
            new StubAgentTemplateService([agent]),
            new StubWorkflowDefinitionService([workflow]));

        var items = await catalog.GetAllAsync();

        Assert.Contains(items, item =>
            item.Reference == new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, sharedId.ToString("D")) &&
            item.RuntimeKind == AgentRuntimeKind.LlmAgent &&
            item.Name == "Writer" &&
            item.Description == "Writes drafts");
        Assert.Contains(items, item =>
            item.Reference == new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, sharedId.ToString("D")) &&
            item.RuntimeKind == AgentRuntimeKind.WorkflowAgent &&
            item.Name == "Review Flow" &&
            item.Description == "Reviews drafts");
    }

    [Fact]
    public async Task Catalog_FindUsesReferenceKind()
    {
        var sharedId = Guid.NewGuid();
        var catalog = new AgentDefinitionCatalog(
            new StubAgentTemplateService([
                new AgentTemplateDefinition
                {
                    Id = sharedId,
                    AgentName = "Agent"
                }
            ]),
            new StubWorkflowDefinitionService([
                new SavedWorkflowDefinition
                {
                    Id = sharedId,
                    DisplayName = "Workflow"
                }
            ]));

        var workflow = await catalog.FindAsync(new AgentDefinitionReference(
            AgentDefinitionKind.SavedWorkflow,
            sharedId.ToString("D")));
        var missing = await catalog.FindAsync(new AgentDefinitionReference(
            AgentDefinitionKind.SavedWorkflow,
            Guid.NewGuid().ToString("D")));

        Assert.NotNull(workflow);
        Assert.Equal("Workflow", workflow.Name);
        Assert.Null(missing);
    }

    [Fact]
    public async Task RuntimeFactory_RoutesSavedAgentAndWorkflow()
    {
        var llmFactory = new RecordingLlmFactory();
        var workflowFactory = new RecordingWorkflowFactory();
        var factory = new AgentRuntimeFactory(llmFactory, workflowFactory);
        var context = new AgentRuntimeCreationContext
        {
            Configuration = new AppChatConfiguration("model", [])
        };

        var agentRuntime = await factory.CreateAsync(
            new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "agent-1"),
            context);
        var workflowRuntime = await factory.CreateAsync(
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "workflow-1"),
            context);

        Assert.Equal("agent-1", llmFactory.LastId);
        Assert.Equal("workflow-1", workflowFactory.LastId);
        Assert.Equal(AgentRuntimeKind.LlmAgent, agentRuntime.Descriptor.Kind);
        Assert.Equal(AgentRuntimeKind.WorkflowAgent, workflowRuntime.Descriptor.Kind);
    }

    private sealed class StubAgentTemplateService(IReadOnlyCollection<AgentTemplateDefinition> agents)
        : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() => Task.FromResult(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == templateId));

        public Task CreateAsync(AgentTemplateDefinition template) => Task.CompletedTask;

        public Task UpdateAsync(AgentTemplateDefinition template) => Task.CompletedTask;

        public Task DeleteAsync(Guid templateId) => Task.CompletedTask;
    }

    private sealed class StubWorkflowDefinitionService(IReadOnlyCollection<SavedWorkflowDefinition> workflows)
        : IWorkflowDefinitionService
    {
        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() => Task.FromResult(workflows);

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult(workflows.FirstOrDefault(workflow => workflow.Id == workflowId));

        public Task CreateAsync(SavedWorkflowDefinition workflow) => Task.CompletedTask;

        public Task UpdateAsync(SavedWorkflowDefinition workflow) => Task.CompletedTask;

        public Task DeleteAsync(Guid workflowId) => Task.CompletedTask;
    }

    private sealed class RecordingLlmFactory : ILlmAgentRuntimeFactory
    {
        public string? LastId { get; private set; }

        public Task<IAgentRuntime> CreateAsync(
            string agentId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default)
        {
            LastId = agentId;
            return Task.FromResult<IAgentRuntime>(new StubRuntime(AgentRuntimeKind.LlmAgent));
        }
    }

    private sealed class RecordingWorkflowFactory : IWorkflowAgentRuntimeFactory
    {
        public string? LastId { get; private set; }

        public Task<IAgentRuntime> CreateAsync(
            string workflowId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default)
        {
            LastId = workflowId;
            return Task.FromResult<IAgentRuntime>(new StubRuntime(AgentRuntimeKind.WorkflowAgent));
        }
    }

    private sealed class StubRuntime(AgentRuntimeKind kind) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } = new("id", "name", string.Empty, kind);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
