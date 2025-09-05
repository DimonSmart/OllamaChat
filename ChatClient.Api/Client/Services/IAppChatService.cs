using ChatClient.Shared.Models;


using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;

public interface IAppChatService
{
    bool IsAnswering { get; }
    Guid Id { get; }
    IReadOnlyCollection<AgentDescription> AgentDescriptions { get; }
    IReadOnlyCollection<IAppChatMessage> Messages { get; }

    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;

    void InitializeChat(IReadOnlyCollection<AgentDescription> initialAgents);
    void ResetChat();
    Task CancelAsync();

    Task GenerateAnswerAsync(string text, AppChatConfiguration chatConfiguration, GroupChatManager groupChatManager, IReadOnlyList<AppChatMessageFile>? files = null);

    Task LoadHistoryAsync(IEnumerable<IAppChatMessage> messages);

    Task DeleteMessageAsync(Guid id);
}

