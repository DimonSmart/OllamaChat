using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticAppChatService : IAgenticAppChatService
{
    private readonly AgenticChatEngineSessionService _engineSessionService;
    private readonly IGroupChatManagerFactory _groupChatManagerFactory;

    public AgenticAppChatService(
        AgenticChatEngineSessionService engineSessionService,
        IGroupChatManagerFactory groupChatManagerFactory)
    {
        _engineSessionService = engineSessionService;
        _groupChatManagerFactory = groupChatManagerFactory;
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

    public Task StartAsync(ChatSessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = parameters.Configuration,
            Agents = parameters.Agents,
            History = parameters.History,
            ChatStrategyName = parameters.GroupChatManager.GetType().Name
        };

        return _engineSessionService.StartAsync(request);
    }

    public void ResetChat() => _engineSessionService.ResetChat();

    public Task CancelAsync() => _engineSessionService.CancelAsync();

    public Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null) =>
        _engineSessionService.SendAsync(text, files);

    public ChatSessionParameters GetState()
    {
        var state = _engineSessionService.GetState();
        var manager = _groupChatManagerFactory.Create("RoundRobin");
        return new ChatSessionParameters(manager, state.Configuration, state.Agents, state.Messages);
    }

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
