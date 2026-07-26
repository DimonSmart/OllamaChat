using ChatClient.Api.Services.Rag;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Tests;

public sealed class RagVectorDataStoreIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ollamachat-rag-{Guid.NewGuid():N}.db");
    private RagVectorDataStore? _store;

    [Fact]
    public async Task SearchAsync_ReturnsResultsFromTheAgentIndex()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RagVectorStore:DatabasePath"] = _databasePath })
            .Build();
        var store = _store = new RagVectorDataStore(configuration);
        var agentId = Guid.NewGuid();

        await store.ReplaceFileAsync(
            agentId,
            "knowledge.md",
            [new RagChunkRecord
            {
                Id = $"{agentId:N}:knowledge.md:0",
                AgentId = agentId.ToString("N"),
                FileName = "knowledge.md",
                Content = "ALPHA-47",
                Embedding = new ReadOnlyMemory<float>([1f, 0f])
            }],
            dimension: 2,
            CancellationToken.None);

        var result = await store.SearchAsync(agentId, new ReadOnlyMemory<float>([1f, 0f]), 5, 0.7, CancellationToken.None);

        var match = Assert.Single(result.Results);
        Assert.Equal("ALPHA-47", match.Content);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null)
            await _store.ClearAsync(CancellationToken.None);
        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
    }
}
