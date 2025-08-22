using System.Text.Json;

using ChatClient.Api.Services;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Tests;

public class RagVectorSearchServiceTests
{
    [Fact]
    public async Task MergesAdjacentFragments()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var agentId = Guid.NewGuid();
            var filesDir = Path.Combine(temp, agentId.ToString(), "files");
            Directory.CreateDirectory(filesDir);
            await File.WriteAllTextAsync(Path.Combine(filesDir, "file1.txt"), "ABCD");

            var store = new VolatileMemoryStore();
            var collection = $"agent_{agentId:N}";
            await store.CreateCollectionAsync(collection);

            await AddAsync(store, collection, "file1.txt", 0, 0, 1, new float[] { 1, 0 });
            await AddAsync(store, collection, "file1.txt", 1, 1, 1, new float[] { 1, 0 });
            await AddAsync(store, collection, "file1.txt", 3, 3, 1, new float[] { 0, 1 });

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["RagFiles:BasePath"] = temp })
                .Build();
            IRagVectorSearchService service = new RagVectorSearchService(store, config, NullLogger<RagVectorSearchService>.Instance);

            var results = await service.SearchAsync(agentId, new ReadOnlyMemory<float>(new float[] { 1, 0 }), 2);

            Assert.Equal(2, results.Count);
            Assert.Equal("file1.txt", results[0].FileName);
            Assert.Equal("AB", results[0].Content);
            Assert.Equal("D", results[1].Content);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }

    private static async Task AddAsync(IMemoryStore store, string collection, string file, int index, long offset, int length, float[] vector)
    {
        var meta = JsonSerializer.Serialize(new { file, index, offset, length });
        var record = new MemoryRecord(
            new MemoryRecordMetadata(false, $"{file}#{index:D5}", null!, null!, null!, meta),
            new ReadOnlyMemory<float>(vector),
            $"{file}#{index:D5}",
            null);
        await store.UpsertAsync(collection, record);
    }
}

