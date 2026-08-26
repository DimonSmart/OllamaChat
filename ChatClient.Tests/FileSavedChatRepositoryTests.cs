using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class FileSavedChatRepositoryTests
{
    [Fact]
    public async Task SaveAndGetAllAsync_RoundTripsAgentNameAndRuntimeReferenceAndUsesCamelCaseMetadata()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var chat = CreateChat();
            await repository.SaveAsync(root, chat, TestContext.Current.CancellationToken);

            var summary = Assert.Single(await repository.GetAllAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal(chat.Launch.RuntimeReference, summary.RuntimeReference);
            Assert.Equal(chat.Launch.AgentName, summary.AgentName);
            var json = await File.ReadAllTextAsync(Path.Combine(root, $"{chat.Id:N}.json"), TestContext.Current.CancellationToken);
            Assert.Contains("\"runtimeReference\"", json, StringComparison.Ordinal);
            Assert.Contains("\"agentName\"", json, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveAndGetAsync_RoundTripsWorkflowMessageFieldsWithoutCopyingContentToStatistics()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var message = new AppChatMessage(
                "Workflow response",
                DateTime.UtcNow,
                AppChatRole.Assistant,
                statistics: "technical metadata",
                agentId: "writer",
                agentName: "Writer",
                usage: new ChatRunUsage(10, 20, 30, 2, TimeSpan.FromSeconds(3)));
            var chat = CreateChat();
            chat.Messages = [message];

            await repository.SaveAsync(root, chat, TestContext.Current.CancellationToken);
            var restored = await repository.GetAsync(root, chat.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(restored);
            var restoredMessage = Assert.Single(restored.Messages);
            Assert.Equal(message.Content, restoredMessage.Content);
            Assert.Equal(message.Statistics, restoredMessage.Statistics);
            Assert.Equal(message.AgentId, restoredMessage.AgentId);
            Assert.Equal(message.AgentName, restoredMessage.AgentName);
            Assert.Equal(message.Usage, restoredMessage.Usage);
            Assert.NotEqual(restoredMessage.Content, restoredMessage.Statistics);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveOneValidDocumentWithoutSharedTemporaryFiles()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var chat = CreateChat();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => repository.SaveAsync(root, new SavedChatDocument
            {
                Id = chat.Id,
                Title = chat.Title,
                CreatedAtUtc = chat.CreatedAtUtc,
                UpdatedAtUtc = chat.UpdatedAtUtc.AddSeconds(index),
                Launch = chat.Launch
            }, TestContext.Current.CancellationToken)));

            var path = Path.Combine(root, $"{chat.Id:N}.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(chat.Id, document.RootElement.GetProperty("id").GetGuid());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentCheckpointAndRename_RetainsManualTitleAndLatestTranscript()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var chat = CreateChat();
            await repository.SaveAsync(root, chat, TestContext.Current.CancellationToken);

            var checkpoint = new SavedChatDocument
            {
                Id = chat.Id,
                Title = "Automatic title",
                CreatedAtUtc = chat.CreatedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow,
                Launch = chat.Launch,
                Messages = [new AppChatMessage("latest", DateTime.Now, AppChatRole.Assistant)]
            };
            await Task.WhenAll(
                repository.UpdateAsync(root, chat.Id, current =>
                {
                    current.Title = "Manual title";
                    current.IsTitleManual = true;
                    return current;
                }, TestContext.Current.CancellationToken),
                repository.SaveCheckpointAsync(root, checkpoint, TestContext.Current.CancellationToken));
            await repository.SaveCheckpointAsync(root, checkpoint, TestContext.Current.CancellationToken);

            var saved = await repository.GetAsync(root, chat.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(saved);
            Assert.Equal("Manual title", saved.Title);
            Assert.True(saved.IsTitleManual);
            Assert.Equal("latest", Assert.Single(saved.Messages).Content);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheRequestedSavedChat()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var deleted = CreateChat();
            var retained = CreateChat();
            await repository.SaveAsync(root, deleted, TestContext.Current.CancellationToken);
            await repository.SaveAsync(root, retained, TestContext.Current.CancellationToken);

            await repository.DeleteAsync(root, deleted.Id, TestContext.Current.CancellationToken);

            Assert.Null(await repository.GetAsync(root, deleted.Id, TestContext.Current.CancellationToken));
            Assert.NotNull(await repository.GetAsync(root, retained.Id, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentDeleteAndUpdateCheckpoint_LeavesTheDeletedChatAbsent()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var chat = CreateChat();
            await repository.SaveAsync(root, chat, TestContext.Current.CancellationToken);

            await Task.WhenAll(
                repository.DeleteAsync(root, chat.Id, TestContext.Current.CancellationToken),
                repository.UpdateCheckpointAsync(root, new SavedChatDocument
                {
                    Id = chat.Id,
                    Title = chat.Title,
                    CreatedAtUtc = chat.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Launch = chat.Launch
                }, TestContext.Current.CancellationToken));

            Assert.Null(await repository.GetAsync(root, chat.Id, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static SavedChatDocument CreateChat() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Chat",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        Launch = new SavedChatLaunchSnapshot
        {
            RuntimeReference = new SavedChatRuntimeReference("SavedAgent", Guid.NewGuid().ToString()),
            AgentName = "Test Agent"
        }
    };
}
