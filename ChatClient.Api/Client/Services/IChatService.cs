using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IChatService
{
    bool IsLoading { get; }
    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, Task>? MessageUpdated;
    void InitializeChat(SystemPrompt? initialPrompt);
    void ClearChat();
    void Cancel();
    Task AddUserMessageAndAnswerAsync(string text, IReadOnlyCollection<string> selectedFunctions, string modelName);
}
