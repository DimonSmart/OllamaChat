using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IKnowledgeIndex
{
    Task ReplaceDocumentAsync(KnowledgeDocumentIndexBatch batch, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RagSearchResult>> SearchVectorAsync(
        KnowledgeVectorSearchRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteStoreAsync(Guid knowledgeStoreId, int embeddingDimension, CancellationToken cancellationToken = default);

    Task DeleteDocumentAsync(
        Guid knowledgeStoreId,
        Guid documentId,
        int embeddingDimension,
        CancellationToken cancellationToken = default);
}
