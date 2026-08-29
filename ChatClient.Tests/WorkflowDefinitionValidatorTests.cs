using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowDefinitionValidatorTests
{
    private readonly WorkflowDefinitionValidator _validator = new();

    [Fact]
    public void Validate_RejectsMissingOrDuplicateParticipantsAndDuplicateStartInputs()
    {
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", ParticipantOrder = ["a"] }, "at least one participant");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a"), Participant("A")], ParticipantOrder = ["a"] }, "duplicate participant");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantOrder = ["a"], StartInputs = [new WorkflowStartInputDefinition { Key = "topic", DisplayName = "Topic" }, new WorkflowStartInputDefinition { Key = "TOPIC", DisplayName = "Topic duplicate" }] }, "duplicate start input");
    }

    [Fact]
    public void Validate_RejectsInvalidParticipantSourcesAndOverrides()
    {
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [new WorkflowParticipantDefinition { Id = "a" }], ParticipantOrder = ["a"] }, "no executable source");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Saved("a", "not-a-guid")], ParticipantOrder = ["a"] }, "invalid id");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [new WorkflowParticipantDefinition { Id = "a", Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, Guid.NewGuid().ToString())), Overrides = new WorkflowParticipantOverrides { Llm = new LlmParticipantOverrides() } }], ParticipantOrder = ["a"] }, "LLM overrides");
    }

    [Fact]
    public void Validate_SequentialRejectsEmptyUnknownAndRepeatedOrder()
    {
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")] }, "at least one participant");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantOrder = ["missing"] }, "not defined");
        AssertError(new SequentialWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantOrder = ["a", "a"] }, "more than once");
    }

    [Fact]
    public void Validate_ConcurrentRejectsEmptyUnknownAndRepeatedParticipants()
    {
        AssertError(new ConcurrentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")] }, "at least one participant");
        AssertError(new ConcurrentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantIds = ["missing"] }, "not defined");
        AssertError(new ConcurrentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantIds = ["a", "a"] }, "more than once");
    }

    [Fact]
    public void Validate_GroupChatRejectsParticipantSetAndContinuesManagerValidation()
    {
        AssertError(new GroupChatWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")] }, "at least one participant");
        AssertError(new GroupChatWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantIds = ["missing"] }, "not defined");
        AssertError(new GroupChatWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantIds = ["a", "a"] }, "more than once");
        AssertError(new GroupChatWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], ParticipantIds = ["a"], Manager = new GroupChatWorkflowManagerDefinition { MaximumIterations = 0 } }, "maximum iterations");
    }

    [Fact]
    public void Validate_HandoffRejectsMissingStartAndUnknownEndpoints()
    {
        AssertError(new AgentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")] }, "start agent");
        AssertError(new AgentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], StartParticipantId = "missing" }, "start agent");
        AssertError(new AgentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], StartParticipantId = "a", Handoffs = [new AgentWorkflowHandoffDefinition { FromParticipantId = "missing", ToParticipantId = "a" }] }, "undefined agent");
        AssertError(new AgentWorkflowDefinition { Id = "x", DisplayName = "X", Participants = [Participant("a")], StartParticipantId = "a", Handoffs = [new AgentWorkflowHandoffDefinition { FromParticipantId = "a", ToParticipantId = "missing" }] }, "undefined agent");
    }

    private static WorkflowParticipantDefinition Participant(string id) => new()
    {
        Id = id,
        Source = new InlineAgentParticipantSource(new AgentTemplateDefinition { AgentName = id, Content = "Prompt" })
    };

    private static WorkflowParticipantDefinition Saved(string id, string savedId) => new()
    {
        Id = id,
        Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, savedId))
    };

    private void AssertError(IOrchestrationWorkflowDefinition workflow, string expected) =>
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => _validator.Validate(workflow)).Message, StringComparison.OrdinalIgnoreCase);
}
