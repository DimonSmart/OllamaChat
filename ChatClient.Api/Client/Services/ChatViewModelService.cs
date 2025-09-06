using ChatClient.Api.Client.ViewModels;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public class ChatViewModelService : IChatViewModelService, IAsyncDisposable
{
    private readonly IAppChatService _chatService;
    private readonly List<AppChatMessageViewModel> _messages = [];
    private readonly Action<bool> _answeringStateChangedHandler;
    private readonly Action _chatResetHandler;
    private readonly Func<IAppChatMessage, Task> _messageAddedHandler;
    private readonly Func<IAppChatMessage, bool, Task> _messageUpdatedHandler;
    private readonly Func<Guid, Task> _messageDeletedHandler;

    public IReadOnlyList<AppChatMessageViewModel> Messages => _messages;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<AppChatMessageViewModel, Task>? MessageAdded;
    public event Func<AppChatMessageViewModel, bool, Task>? MessageUpdated;
    public event Func<AppChatMessageViewModel, Task>? MessageDeleted;

    public bool IsAnswering => _chatService.IsAnswering;

    public ChatViewModelService(IAppChatService chatService)
    {
        _chatService = chatService;

        _answeringStateChangedHandler = async isAnswering => await OnAnsweringStateChanged(isAnswering);
        _chatResetHandler = OnChatReset;
        _messageAddedHandler = OnMessageAdded;
        _messageUpdatedHandler = OnMessageUpdated;
        _messageDeletedHandler = OnMessageDeleted;

        _chatService.AnsweringStateChanged += _answeringStateChangedHandler;
        _chatService.ChatReset += _chatResetHandler;
        _chatService.MessageAdded += _messageAddedHandler;
        _chatService.MessageUpdated += _messageUpdatedHandler;
        _chatService.MessageDeleted += _messageDeletedHandler;
    }

    private async Task OnMessageAdded(IAppChatMessage domainMessage)
    {
        // Check if message with this ID already exists to prevent duplicates
        var existingMessage = _messages.FirstOrDefault(m => m.Id == domainMessage.Id);
        if (existingMessage != null)
        {
            // Update existing message instead of adding duplicate
            existingMessage.UpdateFromDomainModel(domainMessage);
            await (MessageUpdated?.Invoke(existingMessage, true) ?? Task.CompletedTask);
            return;
        }

        var viewModel = AppChatMessageViewModel.CreateFromDomainModel(domainMessage);
        _messages.Add(viewModel);
        await (MessageAdded?.Invoke(viewModel) ?? Task.CompletedTask);
    }

    private async Task OnAnsweringStateChanged(bool isAnswering)
    {
        if (!isAnswering)
        {
            foreach (var message in _messages.Where(m => m.IsStreaming))
            {
                message.IsStreaming = false;
                await (MessageUpdated?.Invoke(message, true) ?? Task.CompletedTask);
            }
        }
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    private void OnChatReset()
    {
        _messages.Clear();
        ChatReset?.Invoke();
    }

    private async Task OnMessageUpdated(IAppChatMessage domainMessage, bool forceRender)
    {
        var existingMessage = _messages.FirstOrDefault(m => m.Id == domainMessage.Id);
        if (existingMessage == null)
        {
            return;
        }

        existingMessage.UpdateFromDomainModel(domainMessage);

        await (MessageUpdated?.Invoke(existingMessage, forceRender) ?? Task.CompletedTask);
    }

    private async Task OnMessageDeleted(Guid id)
    {
        var message = _messages.FirstOrDefault(m => m.Id == id);
        if (message == null)
            return;
        _messages.Remove(message);
        await (MessageDeleted?.Invoke(message) ?? Task.CompletedTask);
    }

    public ValueTask DisposeAsync()
    {
        _chatService.AnsweringStateChanged -= _answeringStateChangedHandler;
        _chatService.ChatReset -= _chatResetHandler;
        _chatService.MessageAdded -= _messageAddedHandler;
        _chatService.MessageUpdated -= _messageUpdatedHandler;
        _chatService.MessageDeleted -= _messageDeletedHandler;
        return ValueTask.CompletedTask;
    }
}
