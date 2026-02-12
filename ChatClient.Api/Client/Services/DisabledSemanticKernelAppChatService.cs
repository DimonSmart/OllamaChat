using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services;

public sealed class DisabledSemanticKernelAppChatService : IAppChatService
{
    private const string DisabledMessage =
        "Semantic Kernel chat engine is disabled by configuration (ChatEngine:Mode=Agentic).";

    public event Action<bool>? AnsweringStateChanged
    {
        add { }
        remove { }
    }

    public event Action? ChatReset;

    public event Func<IAppChatMessage, Task>? MessageAdded
    {
        add { }
        remove { }
    }

    public event Func<IAppChatMessage, MessageUpdateOptions, Task>? MessageUpdated
    {
        add { }
        remove { }
    }

    public event Func<Guid, Task>? MessageDeleted
    {
        add { }
        remove { }
    }

    public bool IsAnswering => false;
    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyCollection<AgentDescription> AgentDescriptions => [];
    public IReadOnlyCollection<IAppChatMessage> Messages => [];

    public Task StartAsync(ChatSessionParameters parameters) => Task.FromException(new InvalidOperationException(DisabledMessage));

    public void ResetChat() => ChatReset?.Invoke();

    public Task CancelAsync() => Task.CompletedTask;

    public Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null) => Task.FromException(new InvalidOperationException(DisabledMessage));

    public ChatSessionParameters GetState() => throw new InvalidOperationException(DisabledMessage);

    public Task DeleteMessageAsync(Guid messageId) => Task.CompletedTask;
}
