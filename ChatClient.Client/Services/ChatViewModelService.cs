using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public class ChatViewModelService : IChatViewModelService
{
    private readonly ChatService _chatService;
    private readonly List<ViewModels.ChatMessageViewModel> _messages = new();
    private readonly HashSet<Guid> _processedMessageIds = new();
    private ViewModels.ChatMessageViewModel? _currentStreamingMessage;

    public IReadOnlyList<ViewModels.ChatMessageViewModel> Messages => _messages;

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Action<ViewModels.ChatMessageViewModel>? MessageAdded;
    public event Action<ViewModels.ChatMessageViewModel>? MessageUpdated;
    public event Action? ErrorOccurred;

    public bool IsLoading => _chatService.IsLoading;

    public ChatViewModelService(ChatService chatService)
    {
        _chatService = chatService;
        _chatService.LoadingStateChanged += OnLoadingStateChanged;
        _chatService.ChatInitialized += OnChatInitialized;
        _chatService.MessageReceived += OnMessageReceived;
        _chatService.ErrorOccurred += () => ErrorOccurred?.Invoke();
    }    private void OnLoadingStateChanged(bool isLoading)
    {
        if (!isLoading && _currentStreamingMessage != null)
        {
            _currentStreamingMessage.IsStreaming = false;
            MessageUpdated?.Invoke(_currentStreamingMessage);
            _currentStreamingMessage = null;
        }
        LoadingStateChanged?.Invoke(isLoading);
    }

    private void OnChatInitialized()
    {
        _messages.Clear();
        _processedMessageIds.Clear();
        if (_currentStreamingMessage != null)
        {
            _currentStreamingMessage.IsStreaming = false;
            _currentStreamingMessage = null;
        }
        ChatInitialized?.Invoke();
    }
    
    private async Task OnMessageReceived(IAppChatMessage domainMessage)
    {
        // If this is a new message (not streaming update)
        if (_currentStreamingMessage == null || _currentStreamingMessage.Id != domainMessage.Id)
        {
            var newViewModel = ViewModels.ChatMessageViewModel.FromDomainModel(domainMessage);
            newViewModel.IsStreaming = true;
            _messages.Add(newViewModel);
            _currentStreamingMessage = newViewModel;
            MessageAdded?.Invoke(newViewModel);
        }
        // Update existing streaming message
        else
        {
            _currentStreamingMessage.Content = domainMessage.Content;
            _currentStreamingMessage.HtmlContent = domainMessage.HtmlContent;
            _currentStreamingMessage.Statistics = domainMessage.Statistics;
            MessageUpdated?.Invoke(_currentStreamingMessage);
        }
    }

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        _messages.Clear();
        _currentStreamingMessage = null;
        _chatService.InitializeChat(initialPrompt);
    }

    public void ClearChat()
    {
        _messages.Clear();
        _currentStreamingMessage = null;
        _chatService.ClearChat();
    }

    public void Cancel()
    {
        _chatService.Cancel();
    }

    public Task SendMessageAsync(string text, List<string> selectedFunctions)
    {
        return _chatService.SendMessageAsync(text, selectedFunctions);
    }
}
