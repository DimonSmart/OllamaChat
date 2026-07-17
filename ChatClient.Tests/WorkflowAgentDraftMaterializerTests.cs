using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowAgentDraftMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_LoadsSavedAgentTemplateAndAppliesOverrides()
    {
        var savedAgent = new AgentTemplateDefinition
        {
            Id = Guid.NewGuid(),
            AgentName = "Saved Technical Agent",
            Summary = "Runs technical interviews from the saved-agent catalog.",
            ShortName = "saved-tech",
            Content = "Base prompt"
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .RunAutonomously(maxAutomaticTurns: 4, completionPhase: "complete", completionSummaryLabel: "final")
            .Agent("technical", agent => agent
                .FromSavedAgent("Saved Technical Agent")
                .Role("Technical interviewer")
                .OverrideName("Override name")
                .OverrideAvatarText("TA")
                .OverrideInstructions("Override prompt"))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService([savedAgent]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var technical = Assert.Single(materialized.Participants);
        Assert.NotNull(technical.AgentDraft);
        Assert.Equal("Override name", technical.AgentDraft!.AgentName);
        Assert.Equal("TA", technical.AgentDraft.AvatarText);
        Assert.Equal("Override prompt", technical.AgentDraft.Content);
        Assert.Equal(savedAgent.Id, technical.AgentDraft.Id);
        Assert.Equal("technical", technical.AgentDraft.RuntimeAgentId);
        Assert.Equal("technical", technical.AgentDraft.AgentId);
        Assert.Equal("technical", technical.AgentDraft.ShortName);
        Assert.Equal("Runs technical interviews from the saved-agent catalog.", technical.Summary);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, materialized.Execution.Mode);
        Assert.Equal("final", materialized.Execution.CompletionSummaryLabel);
    }

    [Fact]
    public async Task MaterializeAsync_ThrowsWhenSavedAgentNameIsAmbiguous()
    {
        var savedAgents = new[]
        {
            new AgentTemplateDefinition { Id = Guid.NewGuid(), AgentName = "Duplicate Agent", Content = "First" },
            new AgentTemplateDefinition { Id = Guid.NewGuid(), AgentName = "Duplicate Agent", Content = "Second" }
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .Agent("technical", agent => agent
                .FromSavedAgent("Duplicate Agent"))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService(savedAgents));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync(workflow));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeAsync_AppendsSavedAgentInstructionsWhenRequested()
    {
        var savedAgent = new AgentTemplateDefinition
        {
            Id = Guid.NewGuid(),
            AgentName = "Saved Technical Agent",
            Summary = "Runs technical interviews from the saved-agent catalog.",
            ShortName = "saved-tech",
            Content = "Base prompt"
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .Agent("technical", agent => agent
                .FromSavedAgent("Saved Technical Agent")
                .Role("Technical interviewer")
                .AppendInstructions("Workflow mode:\n- Stay concise."))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService([savedAgent]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var technical = Assert.Single(materialized.Participants);
        Assert.Equal("Base prompt\n\nWorkflow mode:\n- Stay concise.", technical.AgentDraft!.Content);
    }

    [Fact]
    public async Task MaterializeAsync_PreservesExplicitWorkflowSummaryOverSavedAgentSummary()
    {
        var savedAgent = new AgentTemplateDefinition
        {
            Id = Guid.NewGuid(),
            AgentName = "Saved Technical Agent",
            Summary = "Saved-agent summary",
            ShortName = "saved-tech",
            Content = "Base prompt"
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .Agent("technical", agent => agent
                .FromSavedAgent("Saved Technical Agent")
                .Role("Technical interviewer")
                .Summary("Workflow-specific summary"))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService([savedAgent]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var technical = Assert.Single(materialized.Participants);
        Assert.Equal("Workflow-specific summary", technical.Summary);
    }

    [Fact]
    public async Task MaterializeAsync_PreservesProgrammableGroupChatManager()
    {
        var workflow = WorkflowDefinitionBuilder
            .New("debate", "Debate")
            .RunAutonomously(maxAutomaticTurns: 6, completionPhase: "complete", completionSummaryLabel: "final")
            .RequireText("opening_topic", "Opening Topic")
            .Agent("host", agent => agent
                .Role("Host")
                .OverrideAvatarText("H")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Host",
                    ShortName = "host",
                    Content = "Host prompt"
                }))
            .Agent("guest", agent => agent
                .Role("Guest")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Guest",
                    ShortName = "guest",
                    Content = "Guest prompt"
                }))
            .UseGroupChat(groupChat => groupChat
                .Participants("host", "guest")
                .UseProgrammableManager(manager => manager
                    .MaximumIterations(9)
                    .Program(GroupChatManagerPrograms.RoundRobin())))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService([]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var groupChat = Assert.IsType<GroupChatWorkflowDefinition>(materialized);
        Assert.Equal(WorkflowDefinitionKinds.GroupChat, groupChat.Kind);
        Assert.Equal(["host", "guest"], groupChat.ParticipantAgentIds);
        Assert.Equal(GroupChatWorkflowManagerKind.Programmable, groupChat.Manager.Kind);
        Assert.Equal(9, groupChat.Manager.MaximumIterations);
        Assert.NotNull(groupChat.Manager.Program);
        Assert.Equal("RoundRobin", groupChat.Manager.ProgramDisplayName);
        Assert.All(groupChat.Agents, static agent => Assert.NotNull(agent.AgentDraft));
        Assert.All(groupChat.Agents, static agent => Assert.Equal(agent.Id, agent.AgentDraft!.RuntimeAgentId));
        Assert.All(groupChat.Agents, static agent => Assert.Equal(agent.Id, agent.AgentDraft!.ShortName));
        Assert.Equal("H", groupChat.Agents[0].AgentDraft!.AvatarText);
    }

    [Fact]
    public async Task MaterializeAsync_ResolvesAgentDisplayNamePlaceholders_WithoutBreakingSlotBasedDisplayNames()
    {
        var savedAgents = new[]
        {
            new AgentTemplateDefinition
            {
                Id = Guid.NewGuid(),
                AgentName = "Immanuel Kant",
                ShortName = "kant",
                Content = "Kant prompt"
            },
            new AgentTemplateDefinition
            {
                Id = Guid.NewGuid(),
                AgentName = "Friedrich Nietzsche",
                ShortName = "nietzsche",
                Content = "Nietzsche prompt"
            }
        };

        var workflow = WorkflowDefinitionBuilder
            .New("debate", "Debate")
            .Agent("host", agent => agent
                .Role("Host")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Debate Host",
                    ShortName = "host",
                    Content = "Invite {{agent:debater_a.displayName}} and {{agent:debater_b.displayName}}."
                }))
            .Agent("debater_a", agent => agent
                .FromSavedAgent("Immanuel Kant")
                .Role("First debater"))
            .Agent("debater_b", agent => agent
                .FromSavedAgent("Friedrich Nietzsche")
                .Role("Second debater"))
            .UseGroupChat(groupChat => groupChat
                .Participants("host", "debater_a", "debater_b")
                .UseRoundRobinManager())
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentTemplateService(savedAgents));

        var materialized = await materializer.MaterializeAsync(workflow);
        var groupChat = Assert.IsType<GroupChatWorkflowDefinition>(materialized);
        var host = groupChat.Agents.Single(agent => agent.Id == "host");
        var debaterA = groupChat.Agents.Single(agent => agent.Id == "debater_a");
        var debaterB = groupChat.Agents.Single(agent => agent.Id == "debater_b");

        Assert.Equal("Invite Immanuel Kant and Friedrich Nietzsche.", host.AgentDraft!.Content);
        Assert.Equal("Immanuel Kant", debaterA.AgentDraft!.AgentName);
        Assert.Equal("debater_a", debaterA.AgentDraft.RuntimeAgentId);
        Assert.Equal("debater_a", debaterA.AgentDraft.ShortName);
        Assert.Equal("Friedrich Nietzsche", debaterB.AgentDraft!.AgentName);
        Assert.Equal("debater_b", debaterB.AgentDraft.RuntimeAgentId);
        Assert.Equal("debater_b", debaterB.AgentDraft.ShortName);
    }

    private sealed class StubAgentTemplateService(
        IReadOnlyCollection<AgentTemplateDefinition> agents) : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() => Task.FromResult(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid agentId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == agentId));

        public Task CreateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task DeleteAsync(Guid agentId) => throw new NotSupportedException();
    }
}
