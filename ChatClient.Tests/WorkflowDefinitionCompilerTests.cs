using ChatClient.Api.AgentWorkflows;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionCompilerTests
{
    [Fact]
    public async Task CompileAsync_CompilesStarterTemplate()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.InterviewCoachHandoff);

        Assert.Equal("handoff", result.Kind);
        Assert.Equal("interview-coach-fixed-handoff", result.WorkflowId);
        Assert.Equal("Interview Coach Handoff", result.DisplayName);
        Assert.Equal("triage", result.HandoffWorkflow.StartAgentId);
        Assert.Contains(result.HandoffWorkflow.StartInputs, static input => input.Key == "resume");
        Assert.Contains(result.HandoffWorkflow.Agents, static agent => agent.Id == "summarizer");
    }

    [Fact]
    public async Task CompileAsync_UsesWorkflowVariableWhenScriptDoesNotReturnValue()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode =
            """
            var workflow = HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("triage")
                .Agent("triage", agent => agent
                    .Role("Router")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Demo Triage", "triage")
                            .WithInstructions("Test instructions.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Build();
            """;

        var result = await compiler.CompileAsync(sourceCode);

        Assert.Equal("demo", result.WorkflowId);
        Assert.Equal("Demo Workflow", result.DisplayName);
    }

    [Fact]
    public async Task CompileAsync_ThrowsWorkflowCompilationExceptionForInvalidSource()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var exception = await Assert.ThrowsAsync<WorkflowCompilationException>(() =>
            compiler.CompileAsync("var workflow = ;"));

        Assert.Contains("Line 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
