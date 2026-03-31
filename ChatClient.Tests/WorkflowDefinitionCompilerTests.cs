using ChatClient.Api.AgentWorkflows;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionCompilerTests
{
    [Fact]
    public async Task CompileAsync_CompilesStarterTemplate()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.InterviewCoachHandoff);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        Assert.Equal("handoff", result.Kind);
        Assert.Equal("interview-coach-fixed-handoff", result.WorkflowId);
        Assert.Equal("Interview Coach Handoff", result.DisplayName);
        Assert.Equal("triage", workflow.StartAgentId);
        Assert.Contains(workflow.StartInputs, static input => input.Key == "resume");
        Assert.Contains(workflow.Agents, static agent => agent.Id == "summarizer");
    }

    [Fact]
    public async Task CompileAsync_CompilesPhilosopherBattleTemplateWithAutonomousExecution()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.PhilosopherBattleHandoff);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        Assert.Equal("handoff", result.Kind);
        Assert.Equal("philosopher-battle-handoff", result.WorkflowId);
        Assert.Equal("Philosopher Battle: Kant vs Nietzsche", result.DisplayName);
        Assert.Equal("host", workflow.StartAgentId);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, workflow.Execution.Mode);
        Assert.Equal(18, workflow.Execution.MaxAutomaticTurns);
        Assert.Equal("final", workflow.Execution.CompletionSummaryLabel);
        Assert.Contains(workflow.StartInputs, static input => input.Key == "opening_topic");
        Assert.Contains(workflow.Agents, static agent => agent.Id == "kant");
        Assert.Contains(workflow.Agents, static agent => agent.Id == "nietzsche");
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "kant" &&
            handoff.ToAgentId == "host");
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "nietzsche" &&
            handoff.ToAgentId == "host");
    }

    [Fact]
    public async Task CompileAsync_CompilesStarterTemplatesAcrossAllSupportedKinds()
    {
        var compiler = new WorkflowDefinitionCompiler();
        HashSet<string> kinds = [];

        foreach (var template in WorkflowCodeTemplates.StarterTemplates)
        {
            var result = await compiler.CompileAsync(template.SourceCode);

            Assert.Equal(template.WorkflowId, result.WorkflowId);
            Assert.Equal(template.DisplayName, result.DisplayName);
            Assert.Equal(template.Kind, result.Kind);
            Assert.NotNull(result.Workflow);
            kinds.Add(result.Kind);
        }

        Assert.Equal(
            [WorkflowDefinitionKinds.Concurrent, WorkflowDefinitionKinds.GroupChat, WorkflowDefinitionKinds.Handoff, WorkflowDefinitionKinds.Sequential],
            kinds.OrderBy(static kind => kind).ToArray());
    }

    [Fact]
    public async Task CompileAsync_CompilesDefaultGroupChatStarterWithCustomManager()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.PhilosopherBattleGroupChat);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        Assert.Equal(WorkflowDefinitionKinds.GroupChat, result.Kind);
        Assert.Equal("philosopher-battle-group-chat", result.WorkflowId);
        Assert.Equal(GroupChatWorkflowManagerKind.Custom, workflow.Manager.Kind);
        Assert.Equal("philosopher-debate", workflow.Manager.ImplementationKey);
        Assert.Equal(["host", "kant", "nietzsche", "judge"], workflow.ParticipantAgentIds);
    }

    [Fact]
    public async Task CompileAsync_UsesWorkflowVariableWhenScriptDoesNotReturnValue()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .Agent("triage", agent => agent
                    .Role("Router")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Demo Triage", "triage")
                            .WithInstructions("Test instructions.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .UseHandoff(handoff => handoff
                    .StartWith("triage"))
                .Build();
            """;

        var result = await compiler.CompileAsync(sourceCode);

        Assert.Equal("demo", result.WorkflowId);
        Assert.Equal("Demo Workflow", result.DisplayName);
    }

    [Fact]
    public async Task CompileAsync_CompilesWorkflowUsingSavedAgentTemplateSyntax()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .AgentFromSaved("Saved Router", agent => agent
                    .Name("Workflow Router"))
                .UseHandoff(handoff => handoff
                    .StartWith("Saved Router"))
                .Build();

            workflow
            """;

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        var agent = Assert.Single(workflow.Agents);
        Assert.NotNull(agent.SavedAgentTemplate);
        Assert.Equal("Saved Router", agent.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Workflow Router", agent.DraftOverrides.AgentName);
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
