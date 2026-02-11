using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticAppChatService
{
    bool IsAnswering { get; }

    Guid Id { get; }

    IReadOnlyCollection<AgentDescription> AgentDescriptions { get; }

    IReadOnlyCollection<IAppChatMessage> Messages { get; }

    event Action<bool>? AnsweringStateChanged;

    event Action? ChatReset;

    event Func<IAppChatMessage, Task>? MessageAdded;

    event Func<IAppChatMessage, MessageUpdateOptions, Task>? MessageUpdated;

    event Func<Guid, Task>? MessageDeleted;

    Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default);

    void ResetChat();

    Task CancelAsync();

    Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default);

    ChatEngineSessionState GetState();

    Task DeleteMessageAsync(Guid messageId);
}
