using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public interface IChatViewModelService
{
    IReadOnlyList<ViewModels.ChatMessageViewModel> Messages { get; }
    bool IsLoading { get; }

    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Action<ViewModels.ChatMessageViewModel>? MessageAdded;
    event Action<ViewModels.ChatMessageViewModel>? MessageUpdated;
    event Action? ErrorOccurred;

    void InitializeChat(SystemPrompt? initialPrompt);
    void ClearChat();
    void Cancel();
    Task SendMessageAsync(string text, List<string> selectedFunctions);
}
