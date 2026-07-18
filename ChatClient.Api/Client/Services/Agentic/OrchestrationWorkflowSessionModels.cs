using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowSessionStartRequest
{
    public required IOrchestrationWorkflowDefinition Workflow { get; init; }

    public IReadOnlyList<WorkflowRuntimeParticipant> Participants { get; init; } = [];

    public IReadOnlyList<ResolvedChatAgent> Agents { get; init; } = [];

    public required AppChatConfiguration Configuration { get; init; }

    public string? SessionTitle { get; init; }

    public string? SessionDescription { get; init; }

    public IReadOnlyList<OrchestrationWorkflowStartInputValue> StartInputs { get; init; } = [];
}

public sealed class OrchestrationWorkflowStartInputValue
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public string? SourceFile { get; init; }
}
