namespace ChatClient.Application.Services;

public interface IKnowledgeIndexBackgroundService
{
    void RequestRebuild();
    Task DeleteStoreVectorsAsync(Guid storeId, CancellationToken cancellationToken = default);
    Task DeleteDocumentVectorsAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default);
}
