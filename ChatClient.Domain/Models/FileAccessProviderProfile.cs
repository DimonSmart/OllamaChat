namespace ChatClient.Domain.Models;

public enum FileAccessMode
{
    ReadOnly,
    ReadWrite
}

public sealed class FileAccessProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public FileAccessMode AccessMode { get; set; } = FileAccessMode.ReadWrite;
    public bool RequireReadApproval { get; set; } = true;
    public bool RequireWriteApproval { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
