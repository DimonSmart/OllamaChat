using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;

namespace ChatClient.Tests;

public class ChatViewModelServiceTests
{
    private class StubChatService : IChatService
    {
        public bool IsAnswering => false;
        public IReadOnlyCollection<AgentDescription> AgentDescriptions { get; } = [];
        public event Action<bool>? AnsweringStateChanged;
        public event Action? ChatReset;
        public event Func<IAppChatMessage, Task>? MessageAdded;
        public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
        public event Func<Guid, Task>? MessageDeleted;

        public void InitializeChat(IReadOnlyCollection<AgentDescription> initialAgents) { }
        public void ResetChat() { }
        public Task CancelAsync() => Task.CompletedTask;
#pragma warning disable SKEXP0110
        public Task GenerateAnswerAsync(string text, ChatConfiguration chatConfiguration, GroupChatManager groupChatManager, IReadOnlyList<ChatMessageFile> files) => Task.CompletedTask;
#pragma warning restore SKEXP0110
        public Task DeleteMessageAsync(Guid id) => Task.CompletedTask;

        public Task RaiseMessageAdded(IAppChatMessage message) => MessageAdded?.Invoke(message) ?? Task.CompletedTask;
        public Task RaiseMessageUpdated(IAppChatMessage message, bool forceRender = false) => MessageUpdated?.Invoke(message, forceRender) ?? Task.CompletedTask;
        public Task RaiseMessageDeleted(Guid id) => MessageDeleted?.Invoke(id) ?? Task.CompletedTask;
    }

    [Fact]
    public async Task MessageEvents_AreAwaitedInOrder()
    {
        var chatService = new StubChatService();
        var vmService = new ChatViewModelService(chatService);
        var log = new List<string>();

        vmService.MessageAdded += async vm => { log.Add("added-start"); await Task.Delay(10); log.Add("added-end"); };
        vmService.MessageUpdated += async (vm, _) => { log.Add("updated-start"); await Task.Delay(10); log.Add("updated-end"); };
        vmService.MessageDeleted += async vm => { log.Add("deleted-start"); await Task.Delay(10); log.Add("deleted-end"); };

        var msg = new AppChatMessage("hello", DateTime.UtcNow, ChatRole.User);
        await chatService.RaiseMessageAdded(msg);
        log.Add("after-add");

        var updated = new AppChatMessage(msg) { Content = "updated" };
        await chatService.RaiseMessageUpdated(updated);
        log.Add("after-update");

        await chatService.RaiseMessageDeleted(msg.Id);
        log.Add("after-delete");

        Assert.Equal(new[]
        {
            "added-start", "added-end", "after-add",
            "updated-start", "updated-end", "after-update",
            "deleted-start", "deleted-end", "after-delete"
        }, log);
    }

    [Fact]
    public async Task MessageAddedHandler_ExceptionPropagates()
    {
        var chatService = new StubChatService();
        var vmService = new ChatViewModelService(chatService);
        vmService.MessageAdded += vm => throw new InvalidOperationException("boom");
        var msg = new AppChatMessage("hi", DateTime.UtcNow, ChatRole.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatService.RaiseMessageAdded(msg));
    }

    [Fact]
    public async Task MessageUpdatedHandler_ExceptionPropagates()
    {
        var chatService = new StubChatService();
        var vmService = new ChatViewModelService(chatService);
        var msg = new AppChatMessage("hi", DateTime.UtcNow, ChatRole.User);
        await chatService.RaiseMessageAdded(msg);
        vmService.MessageUpdated += (vm, _) => throw new InvalidOperationException("boom");
        var updated = new AppChatMessage(msg) { Content = "new" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatService.RaiseMessageUpdated(updated));
    }

    [Fact]
    public async Task MessageDeletedHandler_ExceptionPropagates()
    {
        var chatService = new StubChatService();
        var vmService = new ChatViewModelService(chatService);
        var msg = new AppChatMessage("hi", DateTime.UtcNow, ChatRole.User);
        await chatService.RaiseMessageAdded(msg);
        vmService.MessageDeleted += vm => throw new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatService.RaiseMessageDeleted(msg.Id));
    }
}

