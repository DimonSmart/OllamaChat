using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgentInputDefinitionProviderTests
{
    [Fact]
    public async Task GetInputsAsync_SavedAgentReturnsNoWorkflowInputs()
    {
        var provider = new AgentInputDefinitionProvider(
            new StubWorkflowDefinitionService([]),
            new StubCompiler(new AgentWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
                StartAgentId = "agent"
            }));

        var inputs = await provider.GetInputsAsync(new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "agent"));

        Assert.Empty(inputs);
    }

    [Fact]
    public async Task GetInputsAsync_WorkflowWithoutInputsReturnsEmptyList()
    {
        var workflowId = Guid.NewGuid();
        var provider = new AgentInputDefinitionProvider(
            new StubWorkflowDefinitionService([new SavedWorkflowDefinition { Id = workflowId, DisplayName = "Workflow", SourceCode = "source" }]),
            new StubCompiler(new AgentWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
                StartAgentId = "agent"
            }));

        var inputs = await provider.GetInputsAsync(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflowId.ToString("D")));

        Assert.Empty(inputs);
    }

    [Fact]
    public async Task GetInputsAsync_PreservesKindsDefaultsAndRequiredFlags()
    {
        var workflowId = Guid.NewGuid();
        var provider = new AgentInputDefinitionProvider(
            new StubWorkflowDefinitionService([new SavedWorkflowDefinition { Id = workflowId, DisplayName = "Workflow", SourceCode = "source" }]),
            new StubCompiler(new AgentWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
                StartAgentId = "agent",
                StartInputs =
                [
                    Input("title", WorkflowStartInputKind.Text, "hello", true),
                    Input("enabled", WorkflowStartInputKind.Boolean, "true", false),
                    Input("count", WorkflowStartInputKind.Number, "42", false),
                    Input("payload", WorkflowStartInputKind.Json, "{\"a\":1}", false),
                    Input("document", WorkflowStartInputKind.MarkdownDocument, "# Doc", true)
                ]
            }));

        var inputs = await provider.GetInputsAsync(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflowId.ToString("D")));

        Assert.Equal(
            [
                AgentInputDefinitionKind.Text,
                AgentInputDefinitionKind.Boolean,
                AgentInputDefinitionKind.Number,
                AgentInputDefinitionKind.Json,
                AgentInputDefinitionKind.MarkdownDocument
            ],
            inputs.Select(static input => input.Kind).ToArray());
        Assert.Equal(["hello", "true", "42", "{\"a\":1}", "# Doc"], inputs.Select(static input => input.DefaultValue).ToArray());
        Assert.Equal([true, false, false, false, true], inputs.Select(static input => input.IsRequired).ToArray());
    }

    private static WorkflowStartInputDefinition Input(
        string key,
        WorkflowStartInputKind kind,
        string defaultValue,
        bool required) =>
        new()
        {
            Key = key,
            DisplayName = key,
            Kind = kind,
            DefaultValue = defaultValue,
            IsRequired = required
        };

    private sealed class StubCompiler(IOrchestrationWorkflowDefinition workflow) : IWorkflowDefinitionCompiler
    {
        public Task<CompiledWorkflowDefinition> CompileAsync(
            string sourceCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompiledWorkflowDefinition
            {
                Kind = workflow.Kind,
                WorkflowId = workflow.Id,
                DisplayName = workflow.DisplayName,
                Workflow = workflow
            });
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
}
