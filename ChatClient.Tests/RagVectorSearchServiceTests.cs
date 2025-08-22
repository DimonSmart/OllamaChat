using System.Text.Json;

using ChatClient.Api.Services;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Tests;

public class RagVectorSearchServiceTests
{
    [Fact]
    public async Task MergesAdjacentFragments()
    {
        var agentId = Guid.NewGuid();

        var store = new VolatileMemoryStore();
        var collection = $"agent_{agentId:N}";
        await store.CreateCollectionAsync(collection);

        await AddAsync(store, collection, "file1.txt", 0, "A", new float[] { 1, 0 });
        await AddAsync(store, collection, "file1.txt", 1, "B", new float[] { 1, 0 });
        await AddAsync(store, collection, "file1.txt", 3, "D", new float[] { 0, 1 });

        IRagVectorSearchService service = new RagVectorSearchService(store, NullLogger<RagVectorSearchService>.Instance);

        var results = await service.SearchAsync(agentId, new ReadOnlyMemory<float>(new float[] { 1, 0 }), 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("file1.txt", results[0].FileName);
        Assert.Equal("AB", results[0].Content);
        Assert.Equal("D", results[1].Content);
    }

    private static async Task AddAsync(IMemoryStore store, string collection, string file, int index, string text, float[] vector)
    {
        var meta = JsonSerializer.Serialize(new { file, index, text });
        var record = new MemoryRecord(
            new MemoryRecordMetadata(false, $"{file}#{index:D5}", null!, null!, null!, meta),
            new ReadOnlyMemory<float>(vector),
            $"{file}#{index:D5}",
            null);
        await store.UpsertAsync(collection, record);
    }
}

