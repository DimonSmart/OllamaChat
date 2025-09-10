namespace ChatClient.Domain.Models;

public class RagSearchResult
{
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
}
