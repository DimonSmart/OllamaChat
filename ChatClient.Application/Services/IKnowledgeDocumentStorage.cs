namespace ChatClient.Application.Services;

public interface IKnowledgeDocumentStorage
{
    Task<string?> ReadCanonicalMarkdownAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenSourceReadAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
    Task WriteAsync(Guid storeId, Guid documentId, string fileName, Stream source, string canonicalMarkdown, CancellationToken cancellationToken = default);
    Task WriteLegacyTextAsync(Guid storeId, Guid documentId, string legacyText, string canonicalMarkdown, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
    Task DeleteStoreAsync(Guid storeId, CancellationToken cancellationToken = default);
}
