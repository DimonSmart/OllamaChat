using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public class ChatViewModelService
{
    private readonly ChatService _chatService;
    public List<ViewModels.ChatMessageViewModel> Messages { get; } = new();

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<Task>? MessageReceived;
    public event Action? ErrorOccurred;

    public bool IsLoading => _chatService.IsLoading;

    public ChatViewModelService(ChatService chatService)
    {
        _chatService = chatService;
        _chatService.LoadingStateChanged += isLoading => LoadingStateChanged?.Invoke(isLoading);
        _chatService.ChatInitialized += () => ChatInitialized?.Invoke();
        _chatService.MessageReceived += OnMessageReceived;
        _chatService.ErrorOccurred += () => ErrorOccurred?.Invoke();
    }

    private async Task OnMessageReceived()
    {
        // Convert all domain messages to view models
        Messages.Clear();
        Messages.AddRange(_chatService.Messages.Select(ViewModels.ChatMessageViewModel.FromDomainModel));
        if (MessageReceived != null)
        {
            await MessageReceived.Invoke();
        }
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
