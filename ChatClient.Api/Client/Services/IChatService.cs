using ChatClient.Shared.Models;
#pragma warning disable SKEXP0110
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Api.Client.Services;

public interface IChatService
{
    bool IsAnswering { get; }
    IReadOnlyCollection<AgentDescription> AgentDescriptions { get; }

    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Func<IAppChatMessage, Task>? MessageAdded;
    event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    event Func<Guid, Task>? MessageDeleted;

    void InitializeChat(IReadOnlyCollection<AgentDescription> initialAgents);
    void ResetChat();
    Task CancelAsync();
    Task GenerateAnswerAsync(string text, ChatConfiguration chatConfiguration, GroupChatManager groupChatManager, IReadOnlyList<ChatMessageFile> files);
    Task DeleteMessageAsync(Guid id);
}
#pragma warning restore SKEXP0110
