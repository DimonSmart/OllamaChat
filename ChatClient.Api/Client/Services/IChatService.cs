using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IChatService
{
    bool IsAnswering { get; }
    IReadOnlyList<AgentDescription> AgentDescriptions { get; }
    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;
    void InitializeChat(IEnumerable<AgentDescription> initialAgents);
    void ResetChat();
    Task CancelAsync();
    Task GenerateAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile> files);
    Task DeleteMessageAsync(Guid id);
}
