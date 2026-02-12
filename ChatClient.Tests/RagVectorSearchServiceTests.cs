using ChatClient.Api.Services.Rag;
using ChatClient.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public class RagVectorSearchServiceTests
{
    [Fact]
    public async Task MergesAdjacentFragments()
    {
        var agentId = Guid.NewGuid();
        var tempPath = Path.Combine(Path.GetTempPath(), $"rag-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RagVectorStore:BasePath"] = tempPath
                })
                .Build();

            IRagVectorStore store = new RagVectorStore(configuration, NullLogger<RagVectorStore>.Instance);
            await store.UpsertFileAsync(
                agentId,
                "file1.txt",
                [
                    new RagVectorStoreEntry("file1.txt", 0, "A", [1, 0]),
                    new RagVectorStoreEntry("file1.txt", 1, "B", [1, 0]),
                    new RagVectorStoreEntry("file1.txt", 3, "D", [0, 1])
                ]);

            IRagVectorSearchService service = new RagVectorSearchService(store, NullLogger<RagVectorSearchService>.Instance);
            var response = await service.SearchAsync(agentId, new ReadOnlyMemory<float>([1, 0]), 2);

            Assert.Equal(2, response.Total);
            Assert.Equal(2, response.Results.Count);
            Assert.Equal("file1.txt", response.Results[0].FileName);
            Assert.Equal("AB", response.Results[0].Content);
            Assert.Equal("D", response.Results[1].Content);
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

