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
        Assert.Equal("Philosopher Battle Handoff", result.DisplayName);
        Assert.Equal("host", workflow.StartAgentId);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, workflow.Execution.Mode);
        Assert.Equal(18, workflow.Execution.MaxAutomaticTurns);
        Assert.Equal("final", workflow.Execution.CompletionSummaryLabel);
        Assert.Contains(workflow.StartInputs, static input => input.Key == "opening_topic");
        var debaterA = Assert.Single(workflow.Agents, static agent => agent.Id == "debater_a");
        var debaterB = Assert.Single(workflow.Agents, static agent => agent.Id == "debater_b");
        Assert.Equal("Immanuel Kant", debaterA.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Friedrich Nietzsche", debaterB.SavedAgentTemplate!.SavedAgentName);
        Assert.NotNull(debaterA.DraftOverrides.AppendedInstructions);
        Assert.NotNull(debaterB.DraftOverrides.AppendedInstructions);
        Assert.Contains("Workflow mode:", debaterA.DraftOverrides.AppendedInstructions, StringComparison.Ordinal);
        Assert.Contains("Workflow mode:", debaterB.DraftOverrides.AppendedInstructions, StringComparison.Ordinal);
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "debater_a" &&
            handoff.ToAgentId == "host");
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "debater_b" &&
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
    public async Task CompileAsync_CompilesDefaultGroupChatStarterWithProgrammableManager()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.PhilosopherBattleGroupChat);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        Assert.Equal(WorkflowDefinitionKinds.GroupChat, result.Kind);
        Assert.Equal("philosopher-battle-group-chat", result.WorkflowId);
        Assert.Equal(GroupChatWorkflowManagerKind.Programmable, workflow.Manager.Kind);
        Assert.NotNull(workflow.Manager.Program);
        Assert.Equal("PrefixCycleSuffix", workflow.Manager.ProgramDisplayName);
        Assert.Null(workflow.Manager.ImplementationKey);
        Assert.Equal(["host", "debater_a", "debater_b", "judge"], workflow.ParticipantAgentIds);
        var host = workflow.Agents.Single(agent => agent.Id == "host");
        Assert.Contains("{{agent:debater_a.displayName}}", host.AgentDraft!.Content, StringComparison.Ordinal);
        Assert.Contains("{{agent:debater_b.displayName}}", host.AgentDraft.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileAsync_CompilesGroupChatWithInlineProgrammableManager()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("demo-group-chat", "Demo Group Chat")
                .Agent("host", agent => agent
                    .Role("Host")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Demo Host", "host")
                            .WithInstructions("Host prompt")
                            .BuildDescription()))
                .Agent("guest", agent => agent
                    .Role("Guest")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Demo Guest", "guest")
                            .WithInstructions("Guest prompt")
                            .BuildDescription()))
                .UseGroupChat(groupChat => groupChat
                    .Participants("host", "guest")
                    .UseProgrammableManager(manager => manager
                        .MaximumIterations(4)
                        .SelectNextSpeaker(ctx => ctx.AssistantMessageIndex % 2 == 0 ? "host" : "guest")))
                .Build();

            workflow
            """;

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        Assert.Equal(GroupChatWorkflowManagerKind.Programmable, workflow.Manager.Kind);
        Assert.NotNull(workflow.Manager.Program);
        Assert.Null(workflow.Manager.ProgramDisplayName);
        Assert.Equal("host", workflow.Manager.Program!.SelectNextSpeaker(
            new GroupChatManagerProgramContext(["host", "guest"], [], 0, 4)));
        Assert.Equal("guest", workflow.Manager.Program.SelectNextSpeaker(
            new GroupChatManagerProgramContext(["host", "guest"], ["host"], 1, 4)));
    }

    [Fact]
    public async Task CompileAsync_CompilesNewWorkflowScaffold()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.NewWorkflowScaffold);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        Assert.Equal(WorkflowDefinitionKinds.Handoff, result.Kind);
        Assert.Equal("new-workflow", result.WorkflowId);
        Assert.Equal("New Workflow", result.DisplayName);
        Assert.Equal("triage", workflow.StartAgentId);
        Assert.Single(workflow.Agents, static agent => agent.Id == "triage");
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
                .Agent("router", agent => agent
                    .FromSavedAgent("Saved Router")
                    .OverrideName("Workflow Router")
                    .OverrideAvatarText("WR")
                    .AppendInstructions("Workflow mode only."))
                .UseHandoff(handoff => handoff
                    .StartWith("router"))
                .Build();

            workflow
            """;

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        var agent = Assert.Single(workflow.Agents);
        Assert.NotNull(agent.SavedAgentTemplate);
        Assert.Equal("Saved Router", agent.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Workflow Router", agent.DraftOverrides.AgentName);
        Assert.Equal("WR", agent.DraftOverrides.AvatarText);
        Assert.Equal("Workflow mode only.", agent.DraftOverrides.AppendedInstructions);
    }

    [Fact]
    public async Task CompileAsync_CompilesGroupChatAvatarTextOverrides()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var result = await compiler.CompileAsync(WorkflowCodeTemplates.PhilosopherBattleGroupChat);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        var host = workflow.Agents.Single(agent => agent.Id == "host");
        var debaterA = workflow.Agents.Single(agent => agent.Id == "debater_a");
        var debaterB = workflow.Agents.Single(agent => agent.Id == "debater_b");
        var judge = workflow.Agents.Single(agent => agent.Id == "judge");

        Assert.Equal("H", host.DraftOverrides.AvatarText);
        Assert.Equal("K", debaterA.DraftOverrides.AvatarText);
        Assert.Equal("N", debaterB.DraftOverrides.AvatarText);
        Assert.Equal("J", judge.DraftOverrides.AvatarText);
        Assert.Equal("Immanuel Kant", debaterA.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Friedrich Nietzsche", debaterB.SavedAgentTemplate!.SavedAgentName);
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
