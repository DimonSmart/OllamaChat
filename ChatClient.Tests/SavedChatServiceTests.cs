using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatClient.Tests;

public class SavedChatServiceTests
{
    private static SavedChat CreateSampleChat(string title = "Test", string participantName = "User")
    {
        var participant = new SavedChatParticipant(participantName.ToLowerInvariant(), participantName, Microsoft.Extensions.AI.ChatRole.User);
        var message = new SavedChatMessage(Guid.NewGuid(), "hello", DateTime.UtcNow, Microsoft.Extensions.AI.ChatRole.User, null, null);
        return new SavedChat(Guid.NewGuid(), title, DateTime.UtcNow, [message], [participant]);
    }

    [Fact]
    public async Task SaveLoadDelete_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = tempDir
                })
                .Build();
            var logger = new LoggerFactory().CreateLogger<SavedChatService>();
            var service = new SavedChatService(config, logger);

            var chat = CreateSampleChat();
            await service.SaveAsync(chat);

            var all = await service.GetAllAsync();
            Assert.Single(all);

            var loaded = await service.GetByIdAsync(chat.Id);
            Assert.NotNull(loaded);
            Assert.Equal(chat.Title, loaded!.Title);

            await service.DeleteAsync(chat.Id);
            all = await service.GetAllAsync();
            Assert.Empty(all);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetAll_ReturnsEmpty_WhenDirectoryMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = tempDir
                })
                .Build();
            var logger = new LoggerFactory().CreateLogger<SavedChatService>();
            var service = new SavedChatService(config, logger);

            var all = await service.GetAllAsync();
            Assert.Empty(all);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_FiltersByTitleAndParticipant()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = tempDir
                })
                .Build();
            var logger = new LoggerFactory().CreateLogger<SavedChatService>();
            var service = new SavedChatService(config, logger);

            var chat1 = CreateSampleChat("First", "Alpha");
            var chat2 = CreateSampleChat("Second", "Beta");
            await service.SaveAsync(chat1);
            await service.SaveAsync(chat2);

            var byTitle = await service.SearchAsync("First");
            Assert.Single(byTitle);
            Assert.Equal(chat1.Id, byTitle[0].Id);

            var byParticipant = await service.SearchAsync("beta");
            Assert.Single(byParticipant);
            Assert.Equal(chat2.Id, byParticipant[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetByIdAsync_ReadsLegacyNumericEnums()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SavedChats:DirectoryPath"] = tempDir
                })
                .Build();
            var logger = new LoggerFactory().CreateLogger<SavedChatService>();
            var service = new SavedChatService(config, logger);

            var id = Guid.NewGuid();
            var msgId = Guid.NewGuid();
            var legacyOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var chat = new SavedChat(id, "Test", DateTime.UtcNow,
                [new SavedChatMessage(msgId, "hi", DateTime.UtcNow, Microsoft.Extensions.AI.ChatRole.User, null, null)],
                [new SavedChatParticipant("user", "User", Microsoft.Extensions.AI.ChatRole.User)]);
            Directory.CreateDirectory(tempDir);
            var json = JsonSerializer.Serialize(chat, legacyOptions);
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"{id}.json"), json);

            var loaded = await service.GetByIdAsync(id);
            Assert.NotNull(loaded);
            Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, loaded!.Messages[0].Role);
            Assert.Equal(Microsoft.Extensions.AI.ChatRole.User, loaded.Participants.First().Role);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
