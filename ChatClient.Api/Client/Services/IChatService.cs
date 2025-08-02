using ChatClient.Shared.Models;
using ChatClient.Shared.Agents;
using System.Collections.Generic;

namespace ChatClient.Api.Client.Services;

public interface IChatService
{
    bool IsLoading { get; }
    IReadOnlyList<SystemPrompt> AgentDescriptions { get; }
    IReadOnlyList<IAgent> ActiveAgents { get; }
    event Action<bool>? LoadingStateChanged;
    event Action? ChatInitialized;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;
    void InitializeChat(IEnumerable<SystemPrompt>? initialAgents);
    void ClearChat();
    Task CancelAsync();
    Task AddUserMessageAndAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile> files);
    Task DeleteMessageAsync(Guid id);
}
