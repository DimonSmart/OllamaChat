using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class SemanticKernelChatEngineSessionService : IChatEngineSessionService
{
    private readonly IAppChatService _innerChatService;
    private readonly IGroupChatManagerFactory _groupChatManagerFactory;

    public SemanticKernelChatEngineSessionService(
        IAppChatService innerChatService,
        IGroupChatManagerFactory groupChatManagerFactory)
    {
        _innerChatService = innerChatService;
        _groupChatManagerFactory = groupChatManagerFactory;
        AttachEvents();
    }

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering => _innerChatService.IsAnswering;

    public Guid Id => _innerChatService.Id;

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _innerChatService.AgentDescriptions;

    public IReadOnlyCollection<IAppChatMessage> Messages => _innerChatService.Messages;

    public Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var strategy = string.IsNullOrWhiteSpace(request.ChatStrategyName) ? "RoundRobin" : request.ChatStrategyName;
        var manager = _groupChatManagerFactory.Create(strategy, request.ChatStrategyOptions);
        var session = new ChatSessionParameters(manager, request.Configuration, request.Agents, request.History);
        return _innerChatService.StartAsync(session);
    }

    public void ResetChat() => _innerChatService.ResetChat();

    public Task CancelAsync() => _innerChatService.CancelAsync();

    public Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default) =>
        _innerChatService.SendAsync(text, files);

    public ChatEngineSessionState GetState()
    {
        var state = _innerChatService.GetState();
        return new ChatEngineSessionState
        {
            Configuration = state.Configuration,
            Agents = state.Agents,
            Messages = state.History,
            ChatStrategyName = state.GroupChatManager.GetType().Name
        };
    }

    public Task DeleteMessageAsync(Guid messageId) => _innerChatService.DeleteMessageAsync(messageId);

    public void AttachEvents()
    {
        _innerChatService.AnsweringStateChanged += OnAnsweringStateChanged;
        _innerChatService.ChatReset += OnChatReset;
        _innerChatService.MessageAdded += OnMessageAdded;
        _innerChatService.MessageUpdated += OnMessageUpdated;
        _innerChatService.MessageDeleted += OnMessageDeleted;
    }

    public void DetachEvents()
    {
        _innerChatService.AnsweringStateChanged -= OnAnsweringStateChanged;
        _innerChatService.ChatReset -= OnChatReset;
        _innerChatService.MessageAdded -= OnMessageAdded;
        _innerChatService.MessageUpdated -= OnMessageUpdated;
        _innerChatService.MessageDeleted -= OnMessageDeleted;
    }

    private void OnAnsweringStateChanged(bool isAnswering) => AnsweringStateChanged?.Invoke(isAnswering);

    private void OnChatReset() => ChatReset?.Invoke();

    private Task OnMessageAdded(IAppChatMessage message) =>
        MessageAdded?.Invoke(message) ?? Task.CompletedTask;

    private Task OnMessageUpdated(IAppChatMessage message, MessageUpdateOptions options) =>
        MessageUpdated?.Invoke(message, options.ForceRender) ?? Task.CompletedTask;

    private Task OnMessageDeleted(Guid messageId) =>
        MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask;
}
