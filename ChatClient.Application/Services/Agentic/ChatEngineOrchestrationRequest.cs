using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineOrchestrationRequest
{
    public required AgentDescription Agent { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<IAppChatMessage> Messages { get; init; }

    public required string UserMessage { get; init; }

    public IReadOnlyList<AppChatMessageFile> Files { get; init; } = [];

    public bool EnableRagContext { get; init; } = true;

    public WhiteboardState? Whiteboard { get; init; }
}
