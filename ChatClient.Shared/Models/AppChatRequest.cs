namespace ChatClient.Shared.Models;

public class AppChatRequest
{
    public List<AppChatMessage> Messages { get; set; } = new();
    public List<string> FunctionNames { get; set; } = [];
}
