using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public class SavedChatRepositoryTests
{
    private static SavedChat CreateChat(string title)
    {
        var participant = new SavedChatParticipant("user", "User", ChatRole.User);
        var message = new SavedChatMessage(Guid.NewGuid(), "hi", DateTime.UtcNow, ChatRole.User, null, null);
        return new SavedChat(Guid.NewGuid(), title, DateTime.UtcNow, [message], [participant]);
    }

    [Fact]
    public async Task SaveAndGetByIdAsync_ReturnsChat()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = dir
                })
                .Build();
            var repo = new SavedChatRepository(config, NullLogger<SavedChatRepository>.Instance);
            var chat = CreateChat("Test");
            await repo.SaveAsync(chat);
            var loaded = await repo.GetByIdAsync(chat.Id);
            Assert.NotNull(loaded);
            Assert.Equal(chat.Id, loaded!.Id);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllChats()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = dir
                })
                .Build();
            var repo = new SavedChatRepository(config, NullLogger<SavedChatRepository>.Instance);
            await repo.SaveAsync(CreateChat("One"));
            await repo.SaveAsync(CreateChat("Two"));
            var all = await repo.GetAllAsync();
            Assert.Equal(2, all.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = dir
                })
                .Build();
            var repo = new SavedChatRepository(config, NullLogger<SavedChatRepository>.Instance);
            var chat = CreateChat("ToDelete");
            await repo.SaveAsync(chat);
            await repo.DeleteAsync(chat.Id);
            var result = await repo.GetByIdAsync(chat.Id);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

