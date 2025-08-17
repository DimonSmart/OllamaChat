using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.AI;

public class ChatHistoryBuilderTests
{
    private sealed class DummySettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync() => Task.FromResult(new UserSettings());
        public Task SaveSettingsAsync(UserSettings settings) => Task.CompletedTask;
    }

    [Fact]
    public async Task BuildChatHistoryAsync_LastAssistantBecomesUser()
    {
        var builder = new ChatHistoryBuilder(new DummySettingsService(), new LoggerFactory().CreateLogger<ChatHistoryBuilder>());
        var messages = new List<IAppChatMessage>
        {
            new AppChatMessage("hi", DateTime.UtcNow, ChatRole.User),
            new AppChatMessage("response", DateTime.UtcNow, ChatRole.Assistant, agentName: "agent")
        };
        var kernel = Kernel.CreateBuilder().Build();
        var history = await builder.BuildChatHistoryAsync(messages, kernel, CancellationToken.None);
        Assert.Equal(AuthorRole.User, history[^1].Role);
    }

    [Fact]
    public void BuildBaseHistory_PreservesOrder()
    {
        var builder = new ChatHistoryBuilder(new DummySettingsService(), new LoggerFactory().CreateLogger<ChatHistoryBuilder>());
        var messages = new List<IAppChatMessage>
        {
            new AppChatMessage("first", DateTime.UtcNow.AddMinutes(2), ChatRole.User),
            new AppChatMessage("second", DateTime.UtcNow.AddMinutes(1), ChatRole.Assistant),
            new AppChatMessage("third", DateTime.UtcNow.AddMinutes(3), ChatRole.User)
        };
        var history = builder.BuildBaseHistory(messages);
        var texts = history
            .Select(m => string.Join(string.Empty, m.Items.OfType<Microsoft.SemanticKernel.TextContent>().Select(t => t.Text)))
            .ToList();
        Assert.Equal(new[] { "first", "second", "third" }, texts);
    }
}
