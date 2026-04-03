using ChatClient.Api.AgentWorkflows;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionCompilerTests
{
    [Fact]
    public async Task CompileAsync_CompilesStarterTemplate()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode = await ReadSeedWorkflowSourceAsync("interview-coach-fixed-handoff.workflow.csx");

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        Assert.Equal("handoff", result.Kind);
        Assert.Equal("interview-coach-fixed-handoff", result.WorkflowId);
        Assert.Equal("Interview Coach Handoff", result.DisplayName);
        Assert.Equal("triage", workflow.StartAgentId);
        Assert.Contains(workflow.StartInputs, static input => input.Key == "resume");
        Assert.Contains(workflow.Agents, static agent => agent.Id == "summarizer");
    }

    [Fact]
    public async Task CompileAsync_CompilesAutonomousHandoffWorkflow()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode =
            """"
            var workflow = WorkflowDefinitionBuilder
                .New("autonomous-review-handoff", "Autonomous Review Handoff")
                .Description("Autonomous review loop with a coordinator and two specialists.")
                .RunAutonomously(maxAutomaticTurns: 12, completionPhase: "complete", completionSummaryLabel: "final")
                .RequireText("topic", "Topic")
                .Agent("coordinator", agent => agent
                    .Role("Coordinator")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Review Coordinator", "coordinator")
                            .WithInstructions("Coordinate the review and close it when complete.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Agent("analyst", agent => agent
                    .FromSavedAgent("Saved Analyst")
                    .AppendInstructions("""
                        Workflow mode:
                        - Review the topic directly.
                        - Answer the other participants, not the user.
                        """))
                .Agent("challenger", agent => agent
                    .FromSavedAgent("Saved Challenger")
                    .AppendInstructions("""
                        Workflow mode:
                        - Challenge weak assumptions.
                        - Answer the other participants, not the user.
                        """))
                .UseHandoff(handoff => handoff
                    .StartWith("coordinator")
                    .Handoff("coordinator", "analyst", "open analysis")
                    .Handoff("coordinator", "challenger", "open challenge")
                    .Handoff("analyst", "challenger", "direct response")
                    .Handoff("challenger", "analyst", "direct response")
                    .Handoff("analyst", "coordinator", "wrap up")
                    .Handoff("challenger", "coordinator", "wrap up"))
                .Build();

            workflow
            """";

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<AgentWorkflowDefinition>(result.Workflow);

        Assert.Equal("handoff", result.Kind);
        Assert.Equal("autonomous-review-handoff", result.WorkflowId);
        Assert.Equal("Autonomous Review Handoff", result.DisplayName);
        Assert.Equal("coordinator", workflow.StartAgentId);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, workflow.Execution.Mode);
        Assert.Equal(12, workflow.Execution.MaxAutomaticTurns);
        Assert.Equal("final", workflow.Execution.CompletionSummaryLabel);
        Assert.Contains(workflow.StartInputs, static input => input.Key == "topic");
        var analyst = Assert.Single(workflow.Agents, static agent => agent.Id == "analyst");
        var challenger = Assert.Single(workflow.Agents, static agent => agent.Id == "challenger");
        Assert.Equal("Saved Analyst", analyst.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Saved Challenger", challenger.SavedAgentTemplate!.SavedAgentName);
        Assert.NotNull(analyst.DraftOverrides.AppendedInstructions);
        Assert.NotNull(challenger.DraftOverrides.AppendedInstructions);
        Assert.Contains("Workflow mode:", analyst.DraftOverrides.AppendedInstructions, StringComparison.Ordinal);
        Assert.Contains("Workflow mode:", challenger.DraftOverrides.AppendedInstructions, StringComparison.Ordinal);
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "analyst" &&
            handoff.ToAgentId == "coordinator");
        Assert.Single(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "challenger" &&
            handoff.ToAgentId == "coordinator");
    }

    [Fact]
    public async Task CompileAsync_CompilesSeededWorkflowsAcrossAllSupportedKinds()
    {
        var compiler = new WorkflowDefinitionCompiler();
        HashSet<string> kinds = [];

        foreach (var template in await LoadSeedWorkflowSourcesAsync())
        {
            var result = await compiler.CompileAsync(template.SourceCode);

            Assert.NotNull(result.Workflow);
            kinds.Add(result.Kind);
        }

        Assert.Equal(
            [WorkflowDefinitionKinds.Concurrent, WorkflowDefinitionKinds.GroupChat, WorkflowDefinitionKinds.Handoff, WorkflowDefinitionKinds.Sequential],
            kinds.OrderBy(static kind => kind).ToArray());
    }

    [Fact]
    public async Task CompileAsync_CompilesSeededGroupChatWorkflowWithProgrammableManager()
    {
        var compiler = new WorkflowDefinitionCompiler();
        var sourceCode = await ReadFirstSeedWorkflowSourceByKindAsync(compiler, WorkflowDefinitionKinds.GroupChat);

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        Assert.Equal(WorkflowDefinitionKinds.GroupChat, result.Kind);
        Assert.Equal(GroupChatWorkflowManagerKind.Programmable, workflow.Manager.Kind);
        Assert.NotNull(workflow.Manager.Program);
        Assert.Equal("PrefixCycleSuffix", workflow.Manager.ProgramDisplayName);
        Assert.Null(workflow.Manager.ImplementationKey);
        Assert.NotEmpty(workflow.ParticipantAgentIds);
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
        var sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("avatar-override-group-chat", "Avatar Override Group Chat")
                .Agent("host", agent => agent
                    .Role("Host")
                    .OverrideAvatarText("H")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Avatar Host", "host")
                            .WithInstructions("Open the discussion.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Agent("reviewer_a", agent => agent
                    .Role("Reviewer A")
                    .OverrideAvatarText("A")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Reviewer A", "reviewer_a")
                            .WithInstructions("Provide the first review.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Agent("reviewer_b", agent => agent
                    .Role("Reviewer B")
                    .OverrideAvatarText("B")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Reviewer B", "reviewer_b")
                            .WithInstructions("Provide the second review.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .Agent("closer", agent => agent
                    .Role("Closer")
                    .OverrideAvatarText("C")
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Review Closer", "closer")
                            .WithInstructions("Close the discussion.")
                            .AutoSelectTools(0)
                            .BuildDescription()))
                .UseGroupChat(groupChat => groupChat
                    .Participants("host", "reviewer_a", "reviewer_b", "closer")
                    .UseProgrammableManager(manager => manager
                        .MaximumIterations(8)
                        .Program(GroupChatManagerPrograms.PrefixCycleSuffix(
                            prefix: new[] { "host" },
                            cycle: new[] { "reviewer_a", "reviewer_b" },
                            suffix: new[] { "reviewer_a", "reviewer_b", "closer" }))))
                .Build();

            workflow
            """;

        var result = await compiler.CompileAsync(sourceCode);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(result.Workflow);

        var host = workflow.Agents.Single(agent => agent.Id == "host");
        var reviewerA = workflow.Agents.Single(agent => agent.Id == "reviewer_a");
        var reviewerB = workflow.Agents.Single(agent => agent.Id == "reviewer_b");
        var closer = workflow.Agents.Single(agent => agent.Id == "closer");

        Assert.Equal("H", host.DraftOverrides.AvatarText);
        Assert.Equal("A", reviewerA.DraftOverrides.AvatarText);
        Assert.Equal("B", reviewerB.DraftOverrides.AvatarText);
        Assert.Equal("C", closer.DraftOverrides.AvatarText);
    }

    [Fact]
    public async Task CompileAsync_ThrowsWorkflowCompilationExceptionForInvalidSource()
    {
        var compiler = new WorkflowDefinitionCompiler();

        var exception = await Assert.ThrowsAsync<WorkflowCompilationException>(() =>
            compiler.CompileAsync("var workflow = ;"));

        Assert.Contains("Line 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadSeedWorkflowSourceAsync(string fileName)
    {
        var path = Path.Combine(GetWorkflowSeedDirectory(), fileName);
        return await File.ReadAllTextAsync(path);
    }

    private static async Task<IReadOnlyList<WorkflowSeedSource>> LoadSeedWorkflowSourcesAsync()
    {
        var seedDirectory = GetWorkflowSeedDirectory();
        List<WorkflowSeedSource> sources = [];

        foreach (var path in Directory.EnumerateFiles(seedDirectory, "*.workflow.csx", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(new WorkflowSeedSource(Path.GetFileName(path), await File.ReadAllTextAsync(path)));
        }

        return sources;
    }

    private static async Task<string> ReadFirstSeedWorkflowSourceByKindAsync(
        WorkflowDefinitionCompiler compiler,
        string workflowKind)
    {
        foreach (var source in await LoadSeedWorkflowSourcesAsync())
        {
            var result = await compiler.CompileAsync(source.SourceCode);
            if (string.Equals(result.Kind, workflowKind, StringComparison.OrdinalIgnoreCase))
            {
                return source.SourceCode;
            }
        }

        throw new InvalidOperationException($"No seed workflow source found for kind '{workflowKind}'.");
    }

    private static string GetWorkflowSeedDirectory()
    {
        return Path.Combine(GetApiRoot(), "Data", "workflows");
    }

    private static string GetApiRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatClient.Api"));
    }

    private sealed record WorkflowSeedSource(string FileName, string SourceCode);
}
