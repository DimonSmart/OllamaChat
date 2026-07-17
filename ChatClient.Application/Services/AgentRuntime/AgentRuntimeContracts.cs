using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.AgentRuntime;

public interface IAgentRuntime
{
    AgentRuntimeDescriptor Descriptor { get; }

    IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        CancellationToken cancellationToken = default);
}

public sealed record AgentRuntimeDescriptor(
    string Id,
    string Name,
    string Description,
    AgentRuntimeKind Kind);

public enum AgentRuntimeKind
{
    LlmAgent,
    WorkflowAgent
}

public sealed record AgentRuntimeRunRequest
{
    public required IReadOnlyList<AgentInputMessage> Messages { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<AgentInputAttachment> Attachments { get; init; } = [];
}

public sealed record AgentInputMessage(
    AgentMessageRole Role,
    string Content);

public enum AgentMessageRole
{
    System,
    User,
    Assistant
}

public sealed record AgentInputAttachment(
    string Name,
    string ContentType,
    string Content)
{
    public byte[] Data { get; init; } = [];
}

public sealed record AgentRunContext
{
    public required string RunId { get; init; }

    public string? ParentRunId { get; init; }

    public string? ConversationId { get; init; }
}

public abstract record AgentRunEvent;

public sealed record AgentTextDelta(
    string MessageId,
    string Author,
    string Text) : AgentRunEvent;

public sealed record AgentMessageCompleted(
    string MessageId,
    AgentOutputMessage Message) : AgentRunEvent;

public sealed record AgentRunCompleted(
    AgentRunResult Result) : AgentRunEvent;

public sealed record AgentRunFailed(
    AgentRunError Error) : AgentRunEvent;

public sealed record AgentRunResult
{
    public required AgentOutputMessage FinalMessage { get; init; }

    public string? FinalMessageId { get; init; }

    public IReadOnlyList<AgentOutputMessage> Messages { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

public sealed record AgentOutputMessage(
    string Author,
    string Content);

public sealed record AgentRunError(
    string Code,
    string Message,
    bool IsRetryable,
    Exception? Exception = null);

public sealed class AgentRuntimeProtocolException(string message)
    : InvalidOperationException(message);

public sealed record AgentDefinitionReference(
    AgentDefinitionKind Kind,
    string Id);

public enum AgentDefinitionKind
{
    SavedAgent,
    SavedWorkflow
}

public interface IAgentDefinitionCatalog
{
    Task<IReadOnlyList<AgentDefinitionCatalogItem>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<AgentDefinitionCatalogItem?> FindAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

public sealed record AgentDefinitionCatalogItem
{
    public required AgentDefinitionReference Reference { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public required AgentRuntimeKind RuntimeKind { get; init; }
}

public interface IAgentRuntimeFactory
{
    Task<IAgentRuntime> CreateAsync(
        AgentDefinitionReference reference,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default);
}

public interface ILlmAgentRuntimeFactory
{
    Task<IAgentRuntime> CreateAsync(
        string agentId,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowAgentRuntimeFactory
{
    Task<IAgentRuntime> CreateAsync(
        string workflowId,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record AgentRuntimeCreationContext
{
    public required AppChatConfiguration Configuration { get; init; }

    public ServerModel? DefaultModel { get; init; }
}

public interface IAgentRunner
{
    IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentDefinitionReference reference,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default);
}
