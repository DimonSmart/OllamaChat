using System;
using ChatClient.Api.Client.ViewModels;
namespace ChatClient.Api.Client.Services;

public interface IChatViewModelService : IAsyncDisposable
{
    IReadOnlyList<AppChatMessageViewModel> Messages { get; }
    bool IsAnswering { get; }
    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<AppChatMessageViewModel, Task>? MessageAdded;
    event Func<AppChatMessageViewModel, MessageUpdateOptions, Task>? MessageUpdated;
    event Func<AppChatMessageViewModel, Task>? MessageDeleted;
}
