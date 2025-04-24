namespace ChatClient.Shared.Models;

public class AppChatRequest
{
    public List<Message> Messages { get; set; } = new List<Message>();
    
    // Optional system prompt ID to use (if not already included in Messages)
    public string? SystemPromptId { get; set; }
}
