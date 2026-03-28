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

    public IReadOnlyList<HandoffWorkflowDocumentInput> Documents { get; init; } = [];
}

public sealed class HandoffWorkflowDocumentInput
{
    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? Markdown { get; init; }

    public string? SourceFile { get; init; }
}
