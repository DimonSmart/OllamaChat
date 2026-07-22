using ChatClient.Api.Client.ViewModels;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatViewModelService : IAgenticChatViewModelService, IAsyncDisposable
{
    private readonly IChatEngineSessionService _chatService;
    private readonly List<AppChatMessageViewModel> _messages = [];
    private readonly Action<bool> _answeringStateChangedHandler;
    private readonly Action _chatResetHandler;
    private readonly Func<IAppChatMessage, Task> _messageAddedHandler;
    private readonly Func<IAppChatMessage, bool, Task> _messageUpdatedHandler;

    public IReadOnlyList<AppChatMessageViewModel> Messages => _messages;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<AppChatMessageViewModel, Task>? MessageAdded;
    public event Func<AppChatMessageViewModel, MessageUpdateOptions, Task>? MessageUpdated;

    public bool IsAnswering => _chatService.IsAnswering;

    public AgenticChatViewModelService(IChatEngineSessionService chatService)
    {
        _chatService = chatService;

        _answeringStateChangedHandler = async isAnswering => await OnAnsweringStateChanged(isAnswering);
        _chatResetHandler = OnChatReset;
        _messageAddedHandler = OnMessageAdded;
        _messageUpdatedHandler = OnMessageUpdated;

        _chatService.AnsweringStateChanged += _answeringStateChangedHandler;
        _chatService.ChatReset += _chatResetHandler;
        _chatService.MessageAdded += _messageAddedHandler;
        _chatService.MessageUpdated += _messageUpdatedHandler;
    }

    private async Task OnMessageAdded(IAppChatMessage domainMessage)
    {
        var existingMessage = _messages.FirstOrDefault(m => m.Id == domainMessage.Id);
        if (existingMessage != null)
        {
            existingMessage.UpdateFromDomainModel(domainMessage);
            await (MessageUpdated?.Invoke(existingMessage, new MessageUpdateOptions(true)) ?? Task.CompletedTask);
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
                await (MessageUpdated?.Invoke(message, new MessageUpdateOptions(true)) ?? Task.CompletedTask);
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
            return;

        existingMessage.UpdateFromDomainModel(domainMessage);
        await (MessageUpdated?.Invoke(existingMessage, new MessageUpdateOptions(forceRender)) ?? Task.CompletedTask);
    }

    public ValueTask DisposeAsync()
    {
        _chatService.AnsweringStateChanged -= _answeringStateChangedHandler;
        _chatService.ChatReset -= _chatResetHandler;
        _chatService.MessageAdded -= _messageAddedHandler;
        _chatService.MessageUpdated -= _messageUpdatedHandler;
        return ValueTask.CompletedTask;
    }
}
