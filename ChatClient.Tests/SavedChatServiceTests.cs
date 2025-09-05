using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class SavedChatServiceTests
{
    private static SavedChat CreateSampleChat()
    {
        var participant = new SavedChatParticipant("user", "User", Microsoft.Extensions.AI.ChatRole.User);
        var message = new SavedChatMessage(Guid.NewGuid(), "hello", DateTime.UtcNow, Microsoft.Extensions.AI.ChatRole.User, null, null);
        return new SavedChat(Guid.NewGuid(), "Test", DateTime.UtcNow, [message], [participant]);
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
}
