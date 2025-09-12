using ChatClient.Api.Services.Rag;
using ChatClient.Application.Services;
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

        var response = await service.SearchAsync(agentId, new ReadOnlyMemory<float>(new float[] { 1, 0 }), 2);

        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal("file1.txt", response.Results[0].FileName);
        Assert.Equal("AB", response.Results[0].Content);
        Assert.Equal("D", response.Results[1].Content);
    }

    private static Task AddAsync(IMemoryStore store, string collection, string file, int index, string text, float[] vector)
    {
        var record = MemoryRecord.LocalRecord($"{file}#{index:D5}", text, null, new ReadOnlyMemory<float>(vector), null, null, null);
        return store.UpsertAsync(collection, record, cancellationToken: default);
    }
}

