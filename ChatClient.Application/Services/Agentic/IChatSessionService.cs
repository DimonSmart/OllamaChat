using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatSessionService
{
    bool IsAnswering { get; }

    bool RequiresReset { get; }

    Guid Id { get; }

    IReadOnlyCollection<AgentExecutionSpec> Agents { get; }

    IReadOnlyCollection<IAppChatMessage> Messages { get; }

    event Action<bool>? AnsweringStateChanged;

    event Action? ChatReset;

    event Func<IAppChatMessage, Task>? MessageAdded;

    event Func<IAppChatMessage, bool, Task>? MessageUpdated;

    Task ResetAsync(CancellationToken cancellationToken = default);

    Task CancelAsync();

    Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default);

}

public interface IEditableChatSessionService : IChatSessionService
{
    event Func<Guid, Task>? MessageDeleted;

    Task DeleteMessageAsync(Guid messageId);
}
