using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Domain.Models;

public class ChatSessionParameters
{
    public ChatSessionParameters(
        GroupChatManager groupChatManager,
        AppChatConfiguration configuration,
        IEnumerable<AgentDescription> agents,
        IEnumerable<IAppChatMessage>? history = null)
    {
        GroupChatManager = groupChatManager ?? throw new ArgumentNullException(nameof(groupChatManager));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Agents = agents?.ToList() ?? throw new ArgumentNullException(nameof(agents));
        History = history?.ToList() ?? [];
    }

    public GroupChatManager GroupChatManager { get; }
    public AppChatConfiguration Configuration { get; }
    public IReadOnlyList<AgentDescription> Agents { get; }
    public IReadOnlyList<IAppChatMessage> History { get; }
}
