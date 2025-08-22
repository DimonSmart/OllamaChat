using ChatClient.Api.Client.ViewModels;
namespace ChatClient.Api.Client.Services;

public interface IChatViewModelService
{
    IReadOnlyList<AppChatMessageViewModel> Messages { get; }
    bool IsAnswering { get; }
    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<AppChatMessageViewModel, Task>? MessageAdded;
    event Func<AppChatMessageViewModel, bool, Task>? MessageUpdated;
    event Func<AppChatMessageViewModel, Task>? MessageDeleted;
}
