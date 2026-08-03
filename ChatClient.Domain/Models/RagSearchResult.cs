namespace ChatClient.Domain.Models;

public class RagSearchResult
{
    public Guid KnowledgeStoreId { get; set; }
    public Guid DocumentId { get; set; }
    public string KnowledgeStoreName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? Section { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public double? VectorScore { get; set; }
    public int? VectorRank { get; set; }
    public int? TextRank { get; set; }
    public int StartChunkIndex { get; set; }
    public int EndChunkIndex { get; set; }
    public bool IsTruncated { get; set; }
}
