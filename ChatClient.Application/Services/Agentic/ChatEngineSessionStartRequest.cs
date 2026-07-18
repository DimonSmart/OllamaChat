using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;

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

    public AgentSessionOverrides Overrides { get; init; } = new();

    public IReadOnlyList<IAppChatMessage> History { get; init; } = [];

    public string ChatStrategyName { get; init; } = "RoundRobin";

    public IChatStrategyOptions? ChatStrategyOptions { get; init; }

    public ChatEngineSessionStartRequest Snapshot() =>
        new()
        {
            Configuration = Configuration,
            Agents = Agents.ToList(),
            RuntimeParticipant = RuntimeParticipant,
            RuntimeReference = RuntimeReference,
            RuntimeDefaultModel = RuntimeDefaultModel,
            RuntimeInputs = new Dictionary<string, string>(
                RuntimeInputs,
                StringComparer.OrdinalIgnoreCase),
            Overrides = Overrides.Snapshot(),
            History = History.ToList(),
            ChatStrategyName = ChatStrategyName,
            ChatStrategyOptions = ChatStrategyOptions
        };
}
