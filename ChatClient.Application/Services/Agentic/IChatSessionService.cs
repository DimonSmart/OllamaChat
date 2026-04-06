using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatSessionService
{
    bool IsAnswering { get; }

    Guid Id { get; }

    IReadOnlyCollection<AgentExecutionSpec> Agents { get; }

    IReadOnlyCollection<IAppChatMessage> Messages { get; }

    event Action<bool>? AnsweringStateChanged;

    event Action? ChatReset;

    event Func<IAppChatMessage, Task>? MessageAdded;

    event Func<IAppChatMessage, bool, Task>? MessageUpdated;

    event Func<Guid, Task>? MessageDeleted;

    void ResetChat();

    Task CancelAsync();

    Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default);

    ChatEngineSessionState GetState();

    Task DeleteMessageAsync(Guid messageId);
}
