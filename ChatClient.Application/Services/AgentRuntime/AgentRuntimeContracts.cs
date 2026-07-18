using ChatClient.Application.Services.Agentic;
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

    public IReadOnlyList<AgentRunFrame> DefinitionStack { get; init; } = [];

    [Obsolete("Use DefinitionStack for runtime logic.")]
    public IReadOnlyList<AgentDefinitionReference> DefinitionPath { get; init; } = [];
}

public sealed record AgentRunFrame
{
    public required AgentDefinitionReference Definition { get; init; }

    public required string DisplayName { get; init; }

    public string? ParticipantId { get; init; }

    public string? ParticipantDisplayName { get; init; }
}

public sealed record WorkflowParticipantInvocation(
    string ParticipantId,
    string ParticipantDisplayName);

public sealed class AgentRuntimeOptions
{
    public int MaximumWorkflowNestingDepth { get; set; } = 8;
}

public interface IAgentRunContextFactory
{
    AgentRunContext CreateRoot(
        AgentDefinitionDescriptor definition,
        string? conversationId = null);

    AgentRunContext CreateChild(
        AgentRunContext parent,
        AgentDefinitionDescriptor childDefinition,
        WorkflowParticipantInvocation? invocation = null);
}

public interface IAgentRunNestingValidator
{
    AgentRunNestingValidation Validate(
        AgentDefinitionDescriptor target,
        AgentRunContext context);
}

public sealed record AgentRunNestingValidation
{
    public required bool IsValid { get; init; }

    public AgentRunError? Error { get; init; }
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

public sealed class AgentRunFailedException : Exception
{
    public AgentRunFailedException(AgentRunError error)
        : base(error.Message, error.Exception)
    {
        Error = error;
    }

    public AgentRunError Error { get; }
}

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
    Exception? Exception = null)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

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
    Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task<AgentDefinitionDescriptor?> FindAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);

    Task<AgentDefinitionDescriptor> GetRequiredAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

public interface IAgentInputDefinitionProvider
{
    Task<IReadOnlyList<AgentInputDefinition>> GetInputsAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

public interface IAgentDefinitionModelRequirementAnalyzer
{
    Task<AgentModelRequirement> AnalyzeAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowDefinitionPreflightValidator
{
    Task<IReadOnlyList<AgentDefinitionLaunchProblem>> ValidateAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default);
}

public interface IAgentDefinitionDependencyGraph
{
    Task<AgentDefinitionDependencyAnalysis> AnalyzeAsync(
        AgentDefinitionReference root,
        CancellationToken cancellationToken = default);
}

public sealed record AgentDefinitionDependencyAnalysis
{
    public required AgentDefinitionReference Root { get; init; }

    public IReadOnlyList<AgentDefinitionDependencyNode> Nodes { get; init; } = [];

    public IReadOnlyList<AgentDefinitionDependencyEdge> Edges { get; init; } = [];

    public IReadOnlyList<AgentDefinitionProblem> Problems { get; init; } = [];
}

public sealed record AgentDefinitionDependencyNode
{
    public required AgentDefinitionReference Definition { get; init; }

    public required string DisplayName { get; init; }

    public required AgentRuntimeKind RuntimeKind { get; init; }
}

public sealed record AgentDefinitionDependencyEdge
{
    public required AgentDefinitionReference Parent { get; init; }

    public required AgentDefinitionReference Child { get; init; }

    public required string ParticipantId { get; init; }

    public required string ParticipantDisplayName { get; init; }
}

public sealed record AgentDefinitionTraversalFrame
{
    public required AgentDefinitionReference Definition { get; init; }

    public required string DisplayName { get; init; }

    public string? ParentParticipantId { get; init; }

    public string? ParentParticipantDisplayName { get; init; }
}

public sealed record AgentInputDefinition
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required AgentInputDefinitionKind Kind { get; init; }

    public bool IsRequired { get; init; }

    public string? Placeholder { get; init; }

    public string? DefaultValue { get; init; }
}

public enum AgentInputDefinitionKind
{
    Text,
    Number,
    Boolean,
    Json,
    MarkdownDocument
}

public enum AgentModelRequirement
{
    None,
    Optional,
    Required
}

public sealed record AgentDefinitionDescriptor
{
    public required AgentDefinitionReference Reference { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public required AgentRuntimeKind RuntimeKind { get; init; }

    public string AvatarText { get; init; } = string.Empty;

    public IReadOnlyList<AgentInputDefinition> Inputs { get; init; } = [];

    public ServerModelSelection ConfiguredModel { get; init; } = new(null, null);

    public AgentModelRequirement ModelRequirement { get; init; }

    public AgentLaunchCapabilities LaunchCapabilities { get; init; } = new();

    public IReadOnlyList<McpServerSessionBinding> DefaultMcpServerBindings { get; init; } = [];

    public bool SupportsConversationHistory { get; init; } = true;

    public bool SupportsAttachments { get; init; }

    public IReadOnlyList<AgentDefinitionProblem> DefinitionProblems { get; init; } = [];

    public bool IsDefinitionValid => DefinitionProblems.Count == 0;
}

public sealed record AgentLaunchCapabilities
{
    public bool SupportsMcpBindingOverrides { get; init; }
}

public sealed record AgentDefinitionLaunchProblem(string Message);

public sealed record AgentDefinitionProblem(string Message);

public sealed record AgentDefinitionLaunchValidation
{
    public required bool CanLaunch { get; init; }

    public IReadOnlyList<AgentDefinitionLaunchProblem> Problems { get; init; } = [];
}

public interface IAgentRuntimeFactory
{
    Task<IAgentRuntime> CreateAsync(
        AgentDefinitionReference reference,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowRuntimeParticipant
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Summary { get; init; }

    public required IAgentRuntime Runtime { get; init; }

    public AgentDefinitionReference? DefinitionReference { get; init; }
}

public interface ILlmAgentRuntimeFactory
{
    Task<IAgentRuntime> CreateAsync(
        string agentId,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default);
}

public interface IInlineLlmAgentRuntimeFactory
{
    IAgentRuntime Create(
        AgentRuntimeDescriptor descriptor,
        AgentTemplateDefinition agent,
        AgentRuntimeCreationContext context);
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

    public AgentSessionOverrides Overrides { get; init; } = new();
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

public interface IAgentRuntimeProtocolExecutor
{
    IAsyncEnumerable<AgentRunEvent> RunAsync(
        IAgentRuntime runtime,
        AgentRuntimeRunRequest request,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default);
}
