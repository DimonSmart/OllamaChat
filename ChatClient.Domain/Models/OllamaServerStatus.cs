namespace ChatClient.Domain.Models;

public class OllamaServerStatus
{
    public bool IsAvailable { get; set; }
    public string? ErrorMessage { get; set; }
}
