namespace ChatClient.Application.Services;

public interface IKnowledgeDocumentStorage
{
    Task<string?> ReadAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
    Task WriteAsync(Guid storeId, Guid documentId, string content, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
    Task DeleteStoreAsync(Guid storeId, CancellationToken cancellationToken = default);
}
