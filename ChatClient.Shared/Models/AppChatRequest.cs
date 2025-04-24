namespace ChatClient.Shared.Models;

public class AppChatRequest
{
    public List<Message> Messages { get; set; } = new List<Message>();
}
