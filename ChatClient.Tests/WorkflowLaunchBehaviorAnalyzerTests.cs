using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowLaunchBehaviorAnalyzerTests
{
    [Theory]
    [InlineData(AgentWorkflowExecutionMode.Interactive, AgentLaunchBehavior.WaitForUserMessage)]
    [InlineData(AgentWorkflowExecutionMode.Autonomous, AgentLaunchBehavior.RunOnStart)]
    public async Task AnalyzeAsync_MapsWorkflowExecutionMode(
        AgentWorkflowExecutionMode mode,
        AgentLaunchBehavior expected)
    {
        var id = Guid.NewGuid();
        var definition = new AgentWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            StartParticipantId = "agent",
            Execution = new AgentWorkflowExecutionDefinition { Mode = mode, MaxAutomaticTurns = 4 }
        };
        var analyzer = new WorkflowLaunchBehaviorAnalyzer(
            new StubWorkflowDefinitionService(id),
            new StubCompiler(definition));

        var behavior = await analyzer.AnalyzeAsync(
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, id.ToString("D")),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, behavior);
    }

    private sealed class StubWorkflowDefinitionService(Guid id) : IWorkflowDefinitionService
    {
        private readonly SavedWorkflowDefinition workflow = new()
        {
            Id = id,
            DisplayName = "Workflow",
            SourceCode = "source"
        };

        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SavedWorkflowDefinition>>([workflow]);

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult<SavedWorkflowDefinition?>(workflowId == id ? workflow : null);

        public Task CreateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();
        public Task UpdateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();
        public Task DeleteAsync(Guid workflowId) => throw new NotSupportedException();
    }

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

}
