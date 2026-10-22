namespace ChatClient.Domain.Models;

public sealed class WorkspaceDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Revision { get; set; }

    public WorkspaceDefinition Clone() => new()
    {
        Id = Id, Name = Name, Path = Path,
        CreatedAtUtc = CreatedAtUtc, UpdatedAtUtc = UpdatedAtUtc, Revision = Revision
    };
}
