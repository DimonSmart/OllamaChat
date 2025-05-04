using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public class ChatViewModelService : IChatViewModelService
{
    private readonly ChatService _chatService;
    private readonly List<ViewModels.ChatMessageViewModel> _messages = new();
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
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        if (!isLoading)
        {
            _currentStreamingMessage = null;
        }
        LoadingStateChanged?.Invoke(isLoading);
    }

    private void OnChatInitialized()
    {
        _messages.Clear();
        _currentStreamingMessage = null;
        ChatInitialized?.Invoke();
    }

    private async Task OnMessageReceived()
    {
        if (_chatService.Messages.Count == 0)
            return;

        var latestDomainMessage = _chatService.Messages[^1];
        
        // If this is a new message (not streaming update)
        if (_currentStreamingMessage == null || _currentStreamingMessage.Role != latestDomainMessage.Role)
        {
            var newViewModel = ViewModels.ChatMessageViewModel.FromDomainModel(latestDomainMessage);
            _messages.Add(newViewModel);
            _currentStreamingMessage = newViewModel;
            MessageAdded?.Invoke(newViewModel);
        }
        // Update existing streaming message
        else
        {
            _currentStreamingMessage.Content = latestDomainMessage.Content;
            _currentStreamingMessage.HtmlContent = latestDomainMessage.HtmlContent;
            _currentStreamingMessage.Statistics = latestDomainMessage.Statistics;            MessageUpdated?.Invoke(_currentStreamingMessage);
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

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        Messages.Clear();
        _chatService.InitializeChat(initialPrompt);
    }

    public void ClearChat()
    {
        Messages.Clear();
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
