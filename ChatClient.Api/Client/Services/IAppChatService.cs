using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IAppChatService
{
    bool IsAnswering { get; }
    Guid Id { get; }
    IReadOnlyCollection<AgentDescription> AgentDescriptions { get; }
    IReadOnlyCollection<IAppChatMessage> Messages { get; }

    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;

    Task StartAsync(ChatSessionParameters parameters);
    void ResetChat();
    Task CancelAsync();
    Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null);
    ChatSessionParameters GetState();
    Task DeleteMessageAsync(Guid id);
}
