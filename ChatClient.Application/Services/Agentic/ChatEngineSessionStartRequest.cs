using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineSessionStartRequest
{
    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<ResolvedChatAgent> Agents { get; init; }

    public IReadOnlyList<IAppChatMessage> History { get; init; } = [];

    public string ChatStrategyName { get; init; } = "RoundRobin";

    public IChatStrategyOptions? ChatStrategyOptions { get; init; }
}
