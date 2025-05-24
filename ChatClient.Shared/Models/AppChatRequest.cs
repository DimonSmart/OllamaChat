namespace ChatClient.Shared.Models;

public class AppChatRequest
{
    public List<AppChatMessage> Messages { get; set; } = [];
    public List<string> FunctionNames { get; set; } = [];
    public string ModelName { get; set; } = string.Empty;
}
