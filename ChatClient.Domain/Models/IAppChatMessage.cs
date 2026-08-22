namespace ChatClient.Domain.Models;

public interface IAppChatMessage : IEquatable<IAppChatMessage>
{
    Guid Id { get; }
    string Content { get; }
    DateTime MsgDateTime { get; }
    AppChatRole Role { get; }
    string? AgentId { get; }
    string? AgentName { get; }
    string? Statistics { get; }
    ChatRunUsage? Usage { get; }
    bool IsStreaming { get; }
    bool IsCanceled { get; }
    IReadOnlyList<AppChatMessageFile> Files { get; }
    IReadOnlyCollection<ToolInvocationViewState> ToolInvocations { get; }
    IReadOnlyCollection<RagRetrievalTrace> RagRetrievals { get; }
}
