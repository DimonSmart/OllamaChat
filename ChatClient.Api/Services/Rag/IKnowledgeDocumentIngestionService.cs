namespace ChatClient.Api.Services.Rag;

public interface IKnowledgeDocumentIngestionService
{
    Task<PreparedKnowledgeDocument> PrepareAsync(string fileName, Stream source, string? contentType = null, CancellationToken cancellationToken = default);
}

public sealed record PreparedKnowledgeDocument(byte[] Source, string CanonicalMarkdown);
