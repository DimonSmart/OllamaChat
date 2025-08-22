using System.Text.Json;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
            var agentFolder = Path.Combine(temp, agentId.ToString());
            Directory.CreateDirectory(Path.Combine(agentFolder, "files"));
            Directory.CreateDirectory(Path.Combine(agentFolder, "index"));

            await File.WriteAllTextAsync(Path.Combine(agentFolder, "files", "file1.txt"), "ABCD");
            var index = new RagVectorIndex
            {
                SourceFileName = "file1.txt",
                SourceModifiedTime = DateTime.UtcNow,
                EmbeddingModel = "test",
                VectorDimensions = 2,
                Fragments =
                [
                    new RagVectorFragment { Id = "file1.txt#00000", Offset = 0, Length = 1, Vector = new float[] { 1, 0 } },
                    new RagVectorFragment { Id = "file1.txt#00001", Offset = 1, Length = 1, Vector = new float[] { 1, 0 } },
                    new RagVectorFragment { Id = "file1.txt#00003", Offset = 3, Length = 1, Vector = new float[] { 0, 1 } }
                ]
            };
            var json = JsonSerializer.Serialize(index);
            await File.WriteAllTextAsync(Path.Combine(agentFolder, "index", "file1.idx"), json);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["RagFiles:BasePath"] = temp })
                .Build();
            IRagVectorSearchService service = new RagVectorSearchService(config, NullLogger<RagVectorSearchService>.Instance);

            var results = await service.SearchAsync(agentId, new float[] { 1, 0 }, 2);

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
}
