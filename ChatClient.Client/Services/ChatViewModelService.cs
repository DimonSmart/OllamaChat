using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public class ChatViewModelService : IChatViewModelService
{
    private readonly IChatService _chatService;
    private readonly List<ViewModels.ChatMessageViewModel> _messages = new();

    public IReadOnlyList<ViewModels.ChatMessageViewModel> Messages => _messages;

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Action<ViewModels.ChatMessageViewModel>? MessageAdded;
    public event Func<ViewModels.ChatMessageViewModel, Task>? MessageUpdated;
    public event Action? ErrorOccurred;

    public bool IsLoading => _chatService.IsLoading;
    public ChatViewModelService(IChatService chatService)
    {
        _chatService = chatService;
        _chatService.LoadingStateChanged += OnLoadingStateChanged;
        _chatService.ChatInitialized += OnChatInitialized;
        _chatService.MessageAdded += OnMessageAdded;
        _chatService.MessageUpdated += OnMessageUpdated;
        _chatService.ErrorOccurred += () => ErrorOccurred?.Invoke();
    }

    private Task OnMessageAdded(IAppChatMessage domainMessage)
    {
        var viewModel = ViewModels.ChatMessageViewModel.CreateFromDomainModel(domainMessage);
        _messages.Add(viewModel);
        MessageAdded?.Invoke(viewModel);
        return Task.CompletedTask;
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        if (!isLoading)
        {
            foreach (var message in _messages.Where(m => m.IsStreaming))
            {
                message.IsStreaming = false;
                MessageUpdated?.Invoke(message);
            }
        }
        LoadingStateChanged?.Invoke(isLoading);
    }
    private void OnChatInitialized()
    {
        _messages.Clear();
        ChatInitialized?.Invoke();
    }
    private async Task OnMessageUpdated(IAppChatMessage domainMessage)
    {
        var existingMessage = _messages.FirstOrDefault(m => m.Id == domainMessage.Id);
        if (existingMessage == null) return;
        existingMessage.UpdateFromDomainModel(domainMessage);
        await (MessageUpdated?.Invoke(existingMessage) ?? Task.CompletedTask);
    }
}
