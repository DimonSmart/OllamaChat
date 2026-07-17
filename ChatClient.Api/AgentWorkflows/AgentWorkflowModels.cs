using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using System.Text.Json.Serialization;

namespace ChatClient.Api.AgentWorkflows;

public sealed class AgentWorkflowTemplate
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required AgentWorkflowDefinition Workflow { get; init; }

    public required AgentWorkflowAssessment Assessment { get; init; }
}

public sealed class AgentWorkflowDefinition : IOrchestrationWorkflowDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Kind => WorkflowDefinitionKinds.Handoff;

    public string StartParticipantId { get; init; } = string.Empty;

    [Obsolete("Use StartParticipantId.")]
    public string StartAgentId
    {
        get => StartParticipantId;
        init => StartParticipantId = value;
    }

    public AgentWorkflowExecutionDefinition Execution { get; init; } = new();

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<WorkflowParticipantDefinition> Participants { get; init; } = [];

    [Obsolete("Use Participants.")]
    public List<WorkflowParticipantDefinition> Agents
    {
        get => Participants;
        init => Participants = value;
    }

    public List<AgentWorkflowHandoffDefinition> Handoffs { get; init; } = [];
}

public sealed class WorkflowStartInputDefinition
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public WorkflowStartInputKind Kind { get; init; }

    public bool IsRequired { get; init; } = true;

    public string? Placeholder { get; init; }

    public string? DefaultValue { get; init; }
}

public enum WorkflowStartInputKind
{
    MarkdownDocument,
    Text,
    Number,
    Boolean,
    Json
}

public class WorkflowParticipantDefinition
{
    public required string Id { get; init; }

    public string Role { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public WorkflowParticipantSource? Source { get; init; }

    public WorkflowParticipantOverrides Overrides { get; init; } = new();

    [Obsolete("Use Source = new InlineAgentParticipantSource(...).")]
    public AgentTemplateDefinition? AgentDraft { get; init; }

    [Obsolete("Use Source = new SavedDefinitionParticipantSource(...).")]
    public AgentWorkflowSavedAgentTemplate? SavedAgentTemplate { get; init; }

    [Obsolete("Use Overrides.")]
    public AgentWorkflowAgentDraftOverrides DraftOverrides { get; init; } = new();

    public List<AgentWorkflowCapabilityRequirement> CapabilityRequirements { get; init; } = [];

    public int? MaxTurnsPerSession { get; init; }

    public int? MinAssistantTurnsBetweenTurns { get; init; }
}

[Obsolete("Use WorkflowParticipantDefinition.")]
public sealed class AgentWorkflowAgentDefinition : WorkflowParticipantDefinition;

public sealed class AgentWorkflowSavedAgentTemplate
{
    public required string SavedAgentName { get; init; }
}

public abstract record WorkflowParticipantSource;

public sealed record InlineAgentParticipantSource(
    AgentTemplateDefinition Agent)
    : WorkflowParticipantSource;

public sealed record SavedDefinitionParticipantSource(
    AgentDefinitionReference Reference)
    : WorkflowParticipantSource;

public sealed record SavedAgentNameParticipantSource(
    string SavedAgentName)
    : WorkflowParticipantSource;

public sealed class WorkflowParticipantOverrides
{
    public string? DisplayName { get; init; }

    public string? Summary { get; init; }

    public LlmParticipantOverrides? Llm { get; init; }
}

public sealed class LlmParticipantOverrides
{
    public string? AvatarText { get; init; }

    public string? Instructions { get; init; }

    public string? AppendedInstructions { get; init; }
}

public sealed class AgentWorkflowAgentDraftOverrides
{
    public string? AgentName { get; init; }

    public string? AvatarText { get; init; }

    public string? Instructions { get; init; }

    public string? AppendedInstructions { get; init; }
}

public sealed class AgentWorkflowHandoffDefinition
{
    public string FromParticipantId { get; init; } = string.Empty;

    public string ToParticipantId { get; init; } = string.Empty;

    [Obsolete("Use FromParticipantId.")]
    public string FromAgentId
    {
        get => FromParticipantId;
        init => FromParticipantId = value;
    }

    [Obsolete("Use ToParticipantId.")]
    public string ToAgentId
    {
        get => ToParticipantId;
        init => ToParticipantId = value;
    }

    public string Label { get; init; } = string.Empty;

    public bool IsFallback { get; init; }
}

public sealed class AgentWorkflowCapabilityRequirement
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string Purpose { get; init; } = string.Empty;

    public AgentWorkflowCapabilityAvailability Availability { get; init; }

    public string AvailabilityNote { get; init; } = string.Empty;
}

public enum AgentWorkflowCapabilityAvailability
{
    Available,
    Partial,
    Missing
}

public sealed class AgentWorkflowAssessment
{
    public bool FluentBuilderIsSufficient { get; init; }

    public string FluentBuilderReason { get; init; } = string.Empty;

    public bool ExistingSavedAgentsAreReusable { get; init; }

    public string ExistingSavedAgentsReason { get; init; } = string.Empty;

    public List<string> ReusableProjectPieces { get; init; } = [];

    public List<string> MissingProjectPieces { get; init; } = [];
}
