using ChatClient.Api.Services;
using ChatClient.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;

namespace ChatClient.Tests;

public class RagVectorSearchServiceTests
{
    [Fact]
    public async Task MergesAdjacentFragments()
    {
        var agentId = Guid.NewGuid();

        var store = new InMemoryVectorStore();
        var collection = store.GetCollection<string, RagVectorRecord>($"agent_{agentId:N}");
        await collection.EnsureCollectionExistsAsync();

        await AddAsync(collection, "file1.txt", 0, "A", new float[] { 1, 0 });
        await AddAsync(collection, "file1.txt", 1, "B", new float[] { 1, 0 });
        await AddAsync(collection, "file1.txt", 3, "D", new float[] { 0, 1 });

        var config = new ConfigurationBuilder().Build();
        IRagVectorSearchService service = new RagVectorSearchService(store, NullLogger<RagVectorSearchService>.Instance, config);

        var response = await service.SearchAsync(agentId, new ReadOnlyMemory<float>(new float[] { 1, 0 }), 2);

        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal("file1.txt", response.Results[0].FileName);
        Assert.Equal("AB", response.Results[0].Content);
        Assert.Equal("D", response.Results[1].Content);
    }

    private static async Task AddAsync(VectorStoreCollection<string, RagVectorRecord> collection, string file, int index, string text, float[] vector)
    {
        var record = new RagVectorRecord
        {
            Id = $"{file}#{index:D5}",
            File = file,
            Index = index,
            Text = text,
            Embedding = new ReadOnlyMemory<float>(vector)
        };
        await collection.UpsertAsync(record);
    }
}

