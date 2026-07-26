namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeIngestionOptions
{
    public const string SectionName = "KnowledgeIngestion";

    public string? MarkItDownMcpEndpoint { get; set; }
}
