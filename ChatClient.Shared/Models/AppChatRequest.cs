namespace ChatClient.Shared.Models;

public class AppChatRequest
{
    public List<Message> Messages { get; set; } = [];
    public List<string> FunctionNames { get; set; } = [];
}
