using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using System.Text.Json.Serialization;

namespace ChatClient.Api.AgentWorkflows;

public interface IOrchestrationWorkflowDefinition
{
    string Kind { get; }

    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    AgentWorkflowExecutionDefinition Execution { get; }

    List<WorkflowStartInputDefinition> StartInputs { get; }

    List<WorkflowParticipantDefinition> Participants { get; }
}

public sealed class SequentialWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public string Kind => WorkflowDefinitionKinds.Sequential;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<WorkflowParticipantDefinition> Participants { get; init; } = [];

    public List<string> ParticipantOrder { get; init; } = [];

    [Obsolete("Use Participants.")]
    public List<WorkflowParticipantDefinition> Agents
    {
        get => Participants;
        init => Participants = value;
    }

    [Obsolete("Use ParticipantOrder.")]
    public List<string> AgentOrder
    {
        get => ParticipantOrder;
        init => ParticipantOrder = value;
    }
}

public sealed class ConcurrentWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public string Kind => WorkflowDefinitionKinds.Concurrent;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<WorkflowParticipantDefinition> Participants { get; init; } = [];

    public List<string> ParticipantIds { get; init; } = [];

    [Obsolete("Use Participants.")]
    public List<WorkflowParticipantDefinition> Agents
    {
        get => Participants;
        init => Participants = value;
    }

    [Obsolete("Use ParticipantIds.")]
    public List<string> ParticipantAgentIds
    {
        get => ParticipantIds;
        init => ParticipantIds = value;
    }

    public ConcurrentWorkflowAggregationDefinition Aggregation { get; init; } = new();
}

public sealed class ConcurrentWorkflowAggregationDefinition
{
    public ConcurrentWorkflowAggregationKind Kind { get; init; } =
        ConcurrentWorkflowAggregationKind.LastMessagePerAgent;
}

public enum ConcurrentWorkflowAggregationKind
{
    LastMessagePerAgent,
    ConcatenateAllMessages
}

public sealed class GroupChatWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public string Kind => WorkflowDefinitionKinds.GroupChat;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<WorkflowParticipantDefinition> Participants { get; init; } = [];

    public List<string> ParticipantIds { get; init; } = [];

    [Obsolete("Use Participants.")]
    public List<WorkflowParticipantDefinition> Agents
    {
        get => Participants;
        init => Participants = value;
    }

    [Obsolete("Use ParticipantIds.")]
    public List<string> ParticipantAgentIds
    {
        get => ParticipantIds;
        init => ParticipantIds = value;
    }

    public GroupChatWorkflowManagerDefinition Manager { get; init; } = new();
}

public sealed class GroupChatWorkflowManagerDefinition
{
    public GroupChatWorkflowManagerKind Kind { get; init; } = GroupChatWorkflowManagerKind.RoundRobin;

    public int MaximumIterations { get; init; } = 40;

    public string? ImplementationKey { get; init; }

    [JsonIgnore]
    public GroupChatManagerProgram? Program { get; init; }

    public string? ProgramDisplayName { get; init; }
}

public enum GroupChatWorkflowManagerKind
{
    RoundRobin,
    Custom,
    Programmable
}
