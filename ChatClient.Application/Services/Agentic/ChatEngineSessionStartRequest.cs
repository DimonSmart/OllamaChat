using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Application.Services.Agentic;

public sealed class ChatEngineSessionStartRequest
{
    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<ResolvedChatAgent> Agents { get; init; }

    public ChatRuntimeParticipantDescriptor? RuntimeParticipant { get; init; }

    public AgentDefinitionReference? RuntimeReference { get; init; }

    public ServerModel? RuntimeDefaultModel { get; init; }

    public IReadOnlyDictionary<string, string> RuntimeInputs { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<IAppChatMessage> History { get; init; } = [];

    public string ChatStrategyName { get; init; } = "RoundRobin";

    public IChatStrategyOptions? ChatStrategyOptions { get; init; }
}
