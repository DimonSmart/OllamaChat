using ChatClient.Application.Services;

namespace ChatClient.Tests;

public sealed class KnowledgeIndexProgressTrackerTests
{
    [Fact]
    public void TracksAndClearsDocumentProgress()
    {
        var tracker = new KnowledgeIndexProgressTracker();
        var storeId = Guid.NewGuid();
        var firstDocumentId = Guid.NewGuid();
        var secondDocumentId = Guid.NewGuid();

        tracker.Begin(storeId, firstDocumentId, 4);
        tracker.Report(storeId, firstDocumentId, 2);
        tracker.Begin(storeId, secondDocumentId, 1);

        Assert.Equal(2, tracker.Get(storeId, firstDocumentId)!.ProcessedChunks);
        tracker.Complete(storeId, firstDocumentId);
        Assert.Null(tracker.Get(storeId, firstDocumentId));
        tracker.ClearStore(storeId);
        Assert.Null(tracker.Get(storeId, secondDocumentId));
    }
}
