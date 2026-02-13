using ChatClient.Api.Services.Rag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public class RagVectorStoreResumeTests
{
    [Fact]
    public async Task ResumesFromLastStoredChunk()
    {
        var agentId = Guid.NewGuid();
        var tempPath = Path.Combine(Path.GetTempPath(), $"rag-resume-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        var databasePath = Path.Combine(tempPath, "rag.sqlite");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RagVectorStore:DatabasePath"] = databasePath
                })
                .Build();

            IRagVectorStore store1 = new RagVectorStore(configuration, NullLogger<RagVectorStore>.Instance);
            var metadata = new RagVectorBuildMetadata(
                SourceHash: "hash-1",
                SourceModifiedUtc: DateTime.UtcNow,
                EmbeddingModel: "bge-m3:latest",
                LineChunkSize: 128,
                ParagraphChunkSize: 256,
                ParagraphOverlap: 32,
                TotalChunks: 3);

            var firstPlan = await store1.BeginIndexingAsync(agentId, "file1.txt", metadata);
            Assert.Equal(0, firstPlan.StartIndex);

            await store1.UpsertEntryAsync(agentId, "file1.txt", new RagVectorStoreEntry("file1.txt", 0, "A", [1, 0]), 1, 3);
            await store1.UpsertEntryAsync(agentId, "file1.txt", new RagVectorStoreEntry("file1.txt", 1, "B", [1, 0]), 2, 3);

            IRagVectorStore store2 = new RagVectorStore(configuration, NullLogger<RagVectorStore>.Instance);
            var resumePlan = await store2.BeginIndexingAsync(agentId, "file1.txt", metadata);
            Assert.Equal(2, resumePlan.StartIndex);
            Assert.False(resumePlan.Rebuilt);

            await store2.UpsertEntryAsync(agentId, "file1.txt", new RagVectorStoreEntry("file1.txt", 2, "C", [1, 0]), 3, 3);
            await store2.CompleteIndexingAsync(agentId, "file1.txt", 3);

            var hasIndex = await store2.HasFileAsync(agentId, "file1.txt");
            Assert.True(hasIndex);

            var entries = await store2.ReadAgentEntriesAsync(agentId);
            Assert.Equal(3, entries.Count);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
