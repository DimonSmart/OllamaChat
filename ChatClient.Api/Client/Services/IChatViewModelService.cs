using ChatClient.Api.Client.ViewModels;
namespace ChatClient.Api.Client.Services;

public interface IChatViewModelService
{
    IReadOnlyList<ChatMessageViewModel> Messages { get; }
    bool IsLoading { get; }
    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Func<ChatMessageViewModel, Task>? MessageAdded;
    event Func<ChatMessageViewModel, bool, Task>? MessageUpdated;
    event Func<ChatMessageViewModel, Task>? MessageDeleted;
}
