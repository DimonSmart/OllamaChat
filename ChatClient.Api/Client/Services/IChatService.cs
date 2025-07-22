using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IChatService
{
    bool IsLoading { get; }
    SystemPrompt? CurrentSystemPrompt { get; }
    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;
    void InitializeChat(SystemPrompt? initialPrompt);
    void ClearChat();
    Task CancelAsync();
    Task AddUserMessageAndAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile> files);
    Task DeleteMessageAsync(Guid id);
}
