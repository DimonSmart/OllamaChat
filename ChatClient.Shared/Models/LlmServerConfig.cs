namespace ChatClient.Shared.Models;

public enum ServerType
{
    Ollama,
    ChatGpt
}

public class LlmServerConfig
{
    public const string DefaultOllamaUrl = "http://localhost:11434";

    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ServerType ServerType { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? Password { get; set; }
    public bool IgnoreSslErrors { get; set; } = false;
    public int HttpTimeoutSeconds { get; set; } = 600;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
