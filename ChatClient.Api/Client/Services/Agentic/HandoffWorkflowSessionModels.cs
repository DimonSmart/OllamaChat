using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HandoffWorkflowSessionStartRequest
{
    public required AgentWorkflowDefinition Workflow { get; init; }

    public required IReadOnlyList<ResolvedChatAgent> Agents { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public string? SessionTitle { get; init; }

    public string? SessionDescription { get; init; }

    public IReadOnlyList<HandoffWorkflowStartInputValue> StartInputs { get; init; } = [];
}

public sealed class HandoffWorkflowStartInputValue
{
    public required string Key { get; init; }

    public string? Value { get; init; }

    public string? SourceFile { get; init; }
}
