using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineSessionState
{
    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<AgentDescription> Agents { get; init; }

    public required IReadOnlyList<IAppChatMessage> Messages { get; init; }

    public string? ChatStrategyName { get; init; }
}
