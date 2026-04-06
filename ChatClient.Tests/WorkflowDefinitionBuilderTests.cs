using ChatClient.Api.AgentWorkflows;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionBuilderTests
{
    [Fact]
    public void Build_HandoffWorkflow_ReturnsHandoffDefinition()
    {
        var workflow = WorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .Agent("triage", agent => agent
                .Role("Router")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Router",
                    ShortName = "triage",
                    Content = "Route requests."
                }))
            .Agent("specialist", agent => agent
                .Role("Specialist")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Specialist",
                    ShortName = "specialist",
                    Content = "Handle work."
                }))
            .UseHandoff(handoff => handoff
                .StartWith("triage")
                .Handoff("triage", "specialist", "route to specialist")
                .Fallback("specialist", "triage"))
            .Build();

        var handoff = Assert.IsType<AgentWorkflowDefinition>(workflow);
        Assert.Equal(WorkflowDefinitionKinds.Handoff, handoff.Kind);
        Assert.Equal("triage", handoff.StartAgentId);
        Assert.Equal(2, handoff.Handoffs.Count);
    }

    [Fact]
    public void Build_ConcurrentWorkflow_UsesDeclaredParticipantsAndSupportedAggregation()
    {
        var workflow = WorkflowDefinitionBuilder
            .New("parallel-review", "Parallel Review")
            .Agent("research", agent => agent
                .Role("Research")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Research",
                    ShortName = "research",
                    Content = "Research prompt"
                }))
            .Agent("review", agent => agent
                .Role("Review")
                .UseDraft(new AgentTemplateDefinition
                {
                    Id = Guid.NewGuid(),
                    AgentName = "Review",
                    ShortName = "review",
                    Content = "Review prompt"
                }))
            .UseConcurrent(concurrent => concurrent
                .Participants("research", "review")
                .ConcatenateAllMessages())
            .Build();

        var concurrent = Assert.IsType<ConcurrentWorkflowDefinition>(workflow);
        Assert.Equal(["research", "review"], concurrent.ParticipantAgentIds);
        Assert.Equal(ConcurrentWorkflowAggregationKind.ConcatenateAllMessages, concurrent.Aggregation.Kind);
    }

    [Fact]
    public void Build_ThrowsForUndefinedAgentPlaceholder()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .Agent("host", agent => agent
                    .Role("Host")
                    .UseDraft(new AgentTemplateDefinition
                    {
                        Id = Guid.NewGuid(),
                        AgentName = "Host",
                        ShortName = "host",
                        Content = "Invite {{agent:missing.displayName}}."
                    }))
                .UseHandoff(handoff => handoff
                    .StartWith("host"))
                .Build());

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GroupChatWorkflow_ValidatesProgrammableManagerProgram()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .Agent("host", agent => agent
                    .Role("Host")
                    .UseDraft(new AgentTemplateDefinition
                    {
                        Id = Guid.NewGuid(),
                        AgentName = "Host",
                        ShortName = "host",
                        Content = "Host prompt"
                    }))
                .UseGroupChat(groupChat => groupChat
                    .Participant("host")
                    .UseProgrammableManager(manager => manager
                        .MaximumIterations(3)))
                .Build());

        Assert.Contains("require a program", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_GroupChatWorkflow_RejectsProgrammableManagerThatReturnsUnknownSpeaker()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .Agent("host", agent => agent
                    .Role("Host")
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
                        .MaximumIterations(3)
                        .SelectNextSpeaker(static _ => "judge")))
                .Build());

        Assert.Contains("unknown speaker 'judge'", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
