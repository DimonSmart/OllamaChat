namespace ChatClient.Domain.Models;

/// <summary>A registration points to an existing local directory; files are never installed or copied.</summary>
public sealed class LocalMarkdownPlugin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Directory { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed record LocalMarkdownPluginInspection(
    Guid Id,
    string Directory,
    bool Enabled,
    string Name,
    string Version,
    string Description,
    string? SkillsDirectory,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Diagnostics);
