namespace ChatClient.Domain.Models;

public sealed class TodoProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public bool SuppressTodoListMessage { get; set; }
    public string? TodoListMessageTemplate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
