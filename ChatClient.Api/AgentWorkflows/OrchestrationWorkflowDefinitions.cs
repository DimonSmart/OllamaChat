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

    List<AgentWorkflowAgentDefinition> Agents { get; }
}

public sealed class SequentialWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public string Kind => WorkflowDefinitionKinds.Sequential;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<AgentWorkflowAgentDefinition> Agents { get; init; } = [];

    public List<string> AgentOrder { get; init; } = [];
}

public sealed class ConcurrentWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public string Kind => WorkflowDefinitionKinds.Concurrent;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<AgentWorkflowAgentDefinition> Agents { get; init; } = [];

    public List<string> ParticipantAgentIds { get; init; } = [];

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

    public List<AgentWorkflowAgentDefinition> Agents { get; init; } = [];

    public List<string> ParticipantAgentIds { get; init; } = [];

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
