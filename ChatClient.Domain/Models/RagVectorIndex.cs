namespace ChatClient.Domain.Models;

public class RagVectorIndex
{
    public string SourceFileName { get; set; } = string.Empty;
    public DateTime SourceModifiedTime { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
    public int VectorDimensions { get; set; }
    public List<RagVectorFragment> Fragments { get; set; } = [];
}
