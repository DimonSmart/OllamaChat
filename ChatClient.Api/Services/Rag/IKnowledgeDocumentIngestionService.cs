using Microsoft.Extensions.DataIngestion;

namespace ChatClient.Api.Services.Rag;

public interface IKnowledgeDocumentIngestionService
{
    Task<PreparedKnowledgeDocument> PrepareAsync(string fileName, Stream source, CancellationToken cancellationToken = default);
    Task<IngestionDocument> ReadCanonicalMarkdownAsync(string fileName, Stream markdown, CancellationToken cancellationToken = default);
}

public sealed record PreparedKnowledgeDocument(byte[] Source, string CanonicalMarkdown);
