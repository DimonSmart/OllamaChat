namespace ChatClient.Application.Services;

public sealed record KnowledgeDocumentIndexProgress(Guid StoreId, Guid DocumentId, int ProcessedChunks, int TotalChunks);

public interface IKnowledgeIndexProgressTracker
{
    KnowledgeDocumentIndexProgress? Get(Guid storeId, Guid documentId);
    void Begin(Guid storeId, Guid documentId, int totalChunks);
    void Report(Guid storeId, Guid documentId, int processedChunks);
    void Complete(Guid storeId, Guid documentId);
    void ClearStore(Guid storeId);
}
