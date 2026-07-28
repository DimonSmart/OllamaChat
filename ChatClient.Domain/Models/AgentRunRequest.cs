namespace ChatClient.Domain.Models;

public sealed class AgentRunRequest
{
    public required AgentExecutionSpec Agent { get; init; }

    public required ServerModel ResolvedModel { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<AgentRunConversationMessage> Conversation { get; init; }

    public required string UserMessage { get; init; }

    public string? WorkspacePath { get; init; }

    public SandboxSessionLaunchConfiguration? Sandbox { get; init; }
}

public sealed record SandboxSessionLaunchConfiguration(
    Guid ProfileId,
    string ProfileName,
    string ProviderType,
    string Configuration);
