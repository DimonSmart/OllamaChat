namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeIngestionOptions
{
    public const string SectionName = "KnowledgeIngestion";

    public string MarkItDownCommand { get; set; } = "markitdown-mcp";
    public string[] MarkItDownArguments { get; set; } = [];
}
