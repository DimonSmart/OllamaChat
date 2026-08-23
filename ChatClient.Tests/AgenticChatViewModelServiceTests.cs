using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Moq;

namespace ChatClient.Tests;

public sealed class AgenticChatViewModelServiceTests
{
    [Fact]
    public async Task Constructor_ProjectsMessagesAlreadyInTheActiveChat()
    {
        var chatService = new Mock<IChatEngineSessionService>();
        IReadOnlyCollection<IAppChatMessage> messages =
        [
            new AppChatMessage("Hola", DateTime.UtcNow, AppChatRole.User),
            new AppChatMessage("Hola!", DateTime.UtcNow, AppChatRole.Assistant)
        ];
        chatService.SetupGet(service => service.Messages).Returns(() => messages);

        await using var service = new AgenticChatViewModelService(chatService.Object);

        Assert.Equal(["Hola", "Hola!"], service.Messages.Select(message => message.RawContent));
    }

    [Fact]
    public async Task ChatReset_ProjectsRestoredMessages()
    {
        var chatService = new Mock<IChatEngineSessionService>();
        IReadOnlyCollection<IAppChatMessage> messages = [];
        chatService.SetupGet(service => service.Messages).Returns(() => messages);
        await using var service = new AgenticChatViewModelService(chatService.Object);

        messages =
        [
            new AppChatMessage("Hola", DateTime.UtcNow, AppChatRole.User),
            new AppChatMessage("LLM response", DateTime.UtcNow, AppChatRole.Assistant)
        ];

        chatService.Raise(chat => chat.ChatReset += null);

        Assert.Equal(["Hola", "LLM response"], service.Messages.Select(message => message.RawContent));
    }
}
