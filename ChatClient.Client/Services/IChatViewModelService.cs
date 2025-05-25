using ChatClient.Client.ViewModels;
namespace ChatClient.Client.Services;

public interface IChatViewModelService
{
    IReadOnlyList<ChatMessageViewModel> Messages { get; }
    bool IsLoading { get; }

    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Action<ChatMessageViewModel>? MessageAdded;
    event Func<ChatMessageViewModel, Task>? MessageUpdated;
    event Action? ErrorOccurred;
}
