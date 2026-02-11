using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticAppChatService : IAgenticAppChatService
{
    private readonly AgenticChatEngineSessionService _engineSessionService;

    public AgenticAppChatService(AgenticChatEngineSessionService engineSessionService)
    {
        _engineSessionService = engineSessionService;
        AttachEvents();
    }

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, MessageUpdateOptions, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering => _engineSessionService.IsAnswering;

    public Guid Id => _engineSessionService.Id;

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _engineSessionService.AgentDescriptions;

    public IReadOnlyCollection<IAppChatMessage> Messages => _engineSessionService.Messages;

    public Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default) =>
        _engineSessionService.StartAsync(request, cancellationToken);

    public void ResetChat() => _engineSessionService.ResetChat();

    public Task CancelAsync() => _engineSessionService.CancelAsync();

    public Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default) =>
        _engineSessionService.SendAsync(text, files, cancellationToken);

    public ChatEngineSessionState GetState() => _engineSessionService.GetState();

    public Task DeleteMessageAsync(Guid messageId) => _engineSessionService.DeleteMessageAsync(messageId);

    public void AttachEvents()
    {
        _engineSessionService.AnsweringStateChanged += OnAnsweringStateChanged;
        _engineSessionService.ChatReset += OnChatReset;
        _engineSessionService.MessageAdded += OnMessageAdded;
        _engineSessionService.MessageUpdated += OnMessageUpdated;
        _engineSessionService.MessageDeleted += OnMessageDeleted;
    }

    public void DetachEvents()
    {
        _engineSessionService.AnsweringStateChanged -= OnAnsweringStateChanged;
        _engineSessionService.ChatReset -= OnChatReset;
        _engineSessionService.MessageAdded -= OnMessageAdded;
        _engineSessionService.MessageUpdated -= OnMessageUpdated;
        _engineSessionService.MessageDeleted -= OnMessageDeleted;
    }

    private void OnAnsweringStateChanged(bool isAnswering) => AnsweringStateChanged?.Invoke(isAnswering);

    private void OnChatReset() => ChatReset?.Invoke();

    private Task OnMessageAdded(IAppChatMessage message) =>
        MessageAdded?.Invoke(message) ?? Task.CompletedTask;

    private Task OnMessageUpdated(IAppChatMessage message, bool forceRender) =>
        MessageUpdated?.Invoke(message, new MessageUpdateOptions(forceRender)) ?? Task.CompletedTask;

    private Task OnMessageDeleted(Guid messageId) =>
        MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask;
}
