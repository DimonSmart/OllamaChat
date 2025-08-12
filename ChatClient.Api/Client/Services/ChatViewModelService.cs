using ChatClient.Api.Client.ViewModels;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public class ChatViewModelService : IChatViewModelService
{
    private readonly IChatService _chatService;
    private readonly List<ChatMessageViewModel> _messages = [];

    public IReadOnlyList<ChatMessageViewModel> Messages => _messages;

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<ChatMessageViewModel, Task>? MessageAdded;
    public event Func<ChatMessageViewModel, bool, Task>? MessageUpdated;
    public event Func<ChatMessageViewModel, Task>? MessageDeleted;

    public bool IsLoading => _chatService.IsLoading;

    public ChatViewModelService(IChatService chatService)
    {
        _chatService = chatService;
        _chatService.LoadingStateChanged += async isLoading => await OnLoadingStateChanged(isLoading);
        _chatService.ChatInitialized += OnChatInitialized;
        _chatService.MessageAdded += OnMessageAdded;
        _chatService.MessageUpdated += OnMessageUpdated;
        _chatService.MessageDeleted += OnMessageDeleted;
    }

    private async Task OnMessageAdded(IAppChatMessage domainMessage)
    {
        var viewModel = ChatMessageViewModel.CreateFromDomainModel(domainMessage);
        _messages.Add(viewModel);
        await (MessageAdded?.Invoke(viewModel) ?? Task.CompletedTask);
    }

    private async Task OnLoadingStateChanged(bool isLoading)
    {
        if (!isLoading)
        {
            foreach (var message in _messages.Where(m => m.IsStreaming))
            {
                message.IsStreaming = false;
                await (MessageUpdated?.Invoke(message, true) ?? Task.CompletedTask);
            }
        }
        LoadingStateChanged?.Invoke(isLoading);
    }
    private void OnChatInitialized()
    {
        _messages.Clear();
        ChatInitialized?.Invoke();
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
        if (message != null)
        {
            _messages.Remove(message);
            await (MessageDeleted?.Invoke(message) ?? Task.CompletedTask);
        }
    }
}
