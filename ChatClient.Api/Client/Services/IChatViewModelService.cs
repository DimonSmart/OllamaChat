using ChatClient.Api.Client.ViewModels;
namespace ChatClient.Api.Client.Services;

public interface IChatViewModelService
{
    IReadOnlyList<ChatMessageViewModel> Messages { get; }
    bool IsAnswering { get; }
    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<ChatMessageViewModel, Task>? MessageAdded;
    event Func<ChatMessageViewModel, bool, Task>? MessageUpdated;
    event Func<ChatMessageViewModel, Task>? MessageDeleted;
}
