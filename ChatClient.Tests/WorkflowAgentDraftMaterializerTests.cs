using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowAgentDraftMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_LoadsSavedAgentTemplateAndAppliesOverrides()
    {
        var savedAgent = new AgentDescription
        {
            Id = Guid.NewGuid(),
            AgentName = "Saved Technical Agent",
            ShortName = "saved-tech",
            Content = "Base prompt"
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .RunAutonomously(maxAutomaticTurns: 4, completionPhase: "complete", completionSummaryLabel: "final")
            .AgentFromSaved("Saved Technical Agent", agent => agent
                .Id("technical")
                .Role("Technical interviewer")
                .Name("Override name")
                .Instructions("Override prompt"))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentDescriptionService([savedAgent]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var technical = Assert.Single(materialized.Agents);
        Assert.NotNull(technical.AgentDraft);
        Assert.Equal("Override name", technical.AgentDraft!.AgentName);
        Assert.Equal("Override prompt", technical.AgentDraft.Content);
        Assert.Equal("technical", technical.AgentDraft.ShortName);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, materialized.Execution.Mode);
        Assert.Equal("final", materialized.Execution.CompletionSummaryLabel);
    }

    [Fact]
    public async Task MaterializeAsync_ThrowsWhenSavedAgentNameIsAmbiguous()
    {
        var savedAgents = new[]
        {
            new AgentDescription { Id = Guid.NewGuid(), AgentName = "Duplicate Agent", Content = "First" },
            new AgentDescription { Id = Guid.NewGuid(), AgentName = "Duplicate Agent", Content = "Second" }
        };

        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .AgentFromSaved("Duplicate Agent", agent => agent.Id("technical"))
            .UseHandoff(handoff => handoff
                .StartWith("technical"))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentDescriptionService(savedAgents));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync(workflow));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeAsync_PreservesGroupChatSpecificFields()
    {
        var workflow = WorkflowDefinitionBuilder
            .New("debate", "Debate")
            .RunAutonomously(maxAutomaticTurns: 6, completionPhase: "complete", completionSummaryLabel: "final")
            .RequireText("opening_topic", "Opening Topic")
            .Agent("host", agent => agent
                .Role("Host")
                .UseDraft(new AgentDescription
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Host",
                    ShortName = "host",
                    Content = "Host prompt"
                }))
            .Agent("guest", agent => agent
                .Role("Guest")
                .UseDraft(new AgentDescription
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Guest",
                    ShortName = "guest",
                    Content = "Guest prompt"
                }))
            .UseGroupChat(groupChat => groupChat
                .Participants("host", "guest")
                .UseCustomManager("debate-manager", maximumIterations: 9))
            .Build();

        var materializer = new WorkflowAgentDraftMaterializer(new StubAgentDescriptionService([]));

        var materialized = await materializer.MaterializeAsync(workflow);

        var groupChat = Assert.IsType<GroupChatWorkflowDefinition>(materialized);
        Assert.Equal(WorkflowDefinitionKinds.GroupChat, groupChat.Kind);
        Assert.Equal(["host", "guest"], groupChat.ParticipantAgentIds);
        Assert.Equal(GroupChatWorkflowManagerKind.Custom, groupChat.Manager.Kind);
        Assert.Equal("debate-manager", groupChat.Manager.ImplementationKey);
        Assert.Equal(9, groupChat.Manager.MaximumIterations);
        Assert.All(groupChat.Agents, static agent => Assert.NotNull(agent.AgentDraft));
        Assert.All(groupChat.Agents, static agent => Assert.Equal(agent.Id, agent.AgentDraft!.ShortName));
    }

    private sealed class StubAgentDescriptionService(
        IReadOnlyCollection<AgentDescription> agents) : IAgentDescriptionService
    {
        public Task<IReadOnlyCollection<AgentDescription>> GetAllAsync() => Task.FromResult(agents);

        public Task<AgentDescription?> GetByIdAsync(Guid agentId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == agentId));

        public Task CreateAsync(AgentDescription agentDescription) => throw new NotSupportedException();

        public Task UpdateAsync(AgentDescription agentDescription) => throw new NotSupportedException();

        public Task DeleteAsync(Guid agentId) => throw new NotSupportedException();
    }
}
