using ChatClient.Api.Client.ViewModels;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public class ChatViewModelService : IChatViewModelService
{
    private readonly IChatService _chatService;
    private readonly List<ChatMessageViewModel> _messages = [];

    public IReadOnlyList<ChatMessageViewModel> Messages => _messages;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<ChatMessageViewModel, Task>? MessageAdded;
    public event Func<ChatMessageViewModel, bool, Task>? MessageUpdated;
    public event Func<ChatMessageViewModel, Task>? MessageDeleted;

    public bool IsAnswering => _chatService.IsAnswering;

    public ChatViewModelService(IChatService chatService)
    {
        _chatService = chatService;
        _chatService.AnsweringStateChanged += async isAnswering => await OnAnsweringStateChanged(isAnswering);
        _chatService.ChatReset += OnChatReset;
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
}
