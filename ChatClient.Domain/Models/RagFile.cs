namespace ChatClient.Domain.Models;

public class RagFile
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool HasIndex { get; set; }
}

