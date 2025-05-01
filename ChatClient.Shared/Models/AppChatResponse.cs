namespace ChatClient.Shared.Models;

public class AppChatResponse
{
    // Use IMessage interface
    public IAppChatMessage Message { get; set; } = null!;
}
