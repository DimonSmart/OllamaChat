using Microsoft.Extensions.AI;
using System.Collections.Generic;

namespace ChatClient.Shared.Models;

public interface IAppChatMessage : IEquatable<IAppChatMessage>
{
    Guid Id { get; }
    string Content { get; }
    DateTime MsgDateTime { get; }
    ChatRole Role { get; }
    string? Statistics { get; }
    bool IsStreaming { get; }
    bool IsCanceled { get; }
    IReadOnlyList<ChatMessageFile> Files { get; }
    IReadOnlyCollection<FunctionCallRecord> FunctionCalls { get; }
}
