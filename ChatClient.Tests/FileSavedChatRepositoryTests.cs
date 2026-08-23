using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class FileSavedChatRepositoryTests
{
    [Fact]
    public async Task SaveAndGetAllAsync_RoundTripsRuntimeReferenceAndUsesCamelCaseMetadata()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileSavedChatRepository(NullLogger<FileSavedChatRepository>.Instance);
            var chat = CreateChat();
            await repository.SaveAsync(root, chat, TestContext.Current.CancellationToken);

            var summary = Assert.Single(await repository.GetAllAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal(chat.Launch.RuntimeReference, summary.RuntimeReference);
            var json = await File.ReadAllTextAsync(Path.Combine(root, $"{chat.Id:N}.json"), TestContext.Current.CancellationToken);
            Assert.Contains("\"runtimeReference\"", json, StringComparison.Ordinal);
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
        Launch = new SavedChatLaunchSnapshot { RuntimeReference = new SavedChatRuntimeReference("SavedAgent", Guid.NewGuid().ToString()) }
    };
}
