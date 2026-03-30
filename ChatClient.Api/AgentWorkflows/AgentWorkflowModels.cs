using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public sealed class AgentWorkflowTemplate
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required AgentWorkflowDefinition Workflow { get; init; }

    public required AgentWorkflowAssessment Assessment { get; init; }
}

public sealed class AgentWorkflowDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required string StartAgentId { get; init; }

    public List<WorkflowStartInputDefinition> StartInputs { get; init; } = [];

    public List<AgentWorkflowAgentDefinition> Agents { get; init; } = [];

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

public sealed class AgentWorkflowAgentDefinition
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    public string Summary { get; init; } = string.Empty;

    public required AgentDescription AgentDraft { get; init; }

    public List<AgentWorkflowCapabilityRequirement> CapabilityRequirements { get; init; } = [];
}

public sealed class AgentWorkflowHandoffDefinition
{
    public required string FromAgentId { get; init; }

    public required string ToAgentId { get; init; }

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
