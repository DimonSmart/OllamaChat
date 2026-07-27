using System.Collections.Concurrent;

namespace ChatClient.Application.Services;

public sealed class KnowledgeIndexProgressTracker : IKnowledgeIndexProgressTracker
{
    private readonly ConcurrentDictionary<(Guid StoreId, Guid DocumentId), KnowledgeDocumentIndexProgress> _progress = new();

    public KnowledgeDocumentIndexProgress? Get(Guid storeId, Guid documentId) =>
        _progress.GetValueOrDefault((storeId, documentId));

    public void Begin(Guid storeId, Guid documentId, int totalChunks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalChunks);
        _progress[(storeId, documentId)] = new KnowledgeDocumentIndexProgress(storeId, documentId, 0, totalChunks);
    }

    public void Report(Guid storeId, Guid documentId, int processedChunks)
    {
        var key = (storeId, documentId);
        _progress.AddOrUpdate(key,
            _ => throw new InvalidOperationException("Indexing progress was not started."),
            (_, progress) => progress with { ProcessedChunks = Math.Clamp(processedChunks, 0, progress.TotalChunks) });
    }

    public void Complete(Guid storeId, Guid documentId) => _progress.TryRemove((storeId, documentId), out _);

    public void ClearStore(Guid storeId)
    {
        foreach (var entry in _progress.Where(entry => entry.Key.StoreId == storeId))
            _progress.TryRemove(entry.Key, out _);
    }
}
