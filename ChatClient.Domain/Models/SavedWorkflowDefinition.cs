namespace ChatClient.Domain.Models;

public sealed class SavedWorkflowDefinition
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = WorkflowDefinitionKinds.Handoff;

    public string WorkflowId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string SourceCode { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public static class WorkflowDefinitionKinds
{
    public const string Handoff = "handoff";
}
