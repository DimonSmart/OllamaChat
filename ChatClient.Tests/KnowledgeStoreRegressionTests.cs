using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public sealed class KnowledgeStoreRegressionTests
{
    [Fact]
    public void GetMarkdown_UsesStructuredMarkdownRepresentation()
    {
        var document = new IngestionDocument("document");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("# First\n\nAAA\n\n## Second\n\nBBB"));
        document.Sections.Add(section);

        var markdown = KnowledgeDocumentIngestionService.GetMarkdown(document);

        Assert.NotEmpty(markdown);
        Assert.Contains("# First", markdown, StringComparison.Ordinal);
        Assert.Contains("## Second", markdown, StringComparison.Ordinal);
        Assert.Contains("AAA", markdown, StringComparison.Ordinal);
        Assert.Contains("BBB", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DimensionMismatchPreservesExistingVectors()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        var documentId = Guid.NewGuid();
        await vectors.ReplaceDocumentAsync(store.Id, documentId, 2,
        [
            new KnowledgeChunkRecord
            {
                Id = $"{store.Id:N}:{documentId:N}:0", KnowledgeStoreId = store.Id.ToString("N"), DocumentId = documentId.ToString("N"),
                FileName = "notes.md", Content = "preserved", Embedding = new float[] { 1, 0 }
            }
        ], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vectors.SearchAsync(store, new float[] { 1, 0, 0 }, 5, -1, CancellationToken.None));

        var results = await vectors.SearchAsync(store, new float[] { 1, 0 }, 5, -1, CancellationToken.None);
        Assert.Contains(results, result => result.Content == "preserved");
    }

    [Fact]
    public async Task UpdateAsync_ConfigurationChangeMakesReadyStoreOutdated()
    {
        var store = CreateReadyStore("Knowledge", 2);
        var repository = new Mock<IKnowledgeStoreRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([store]);
        repository.Setup(service => service.SaveAsync(store, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var indexer = new Mock<IKnowledgeIndexBackgroundService>(MockBehavior.Strict);
        indexer.Setup(service => service.RequestRebuild());
        var service = new KnowledgeStoreService(
            repository.Object,
            Mock.Of<IKnowledgeDocumentStorage>(),
            Mock.Of<IKnowledgeDocumentIngestionService>(),
            Mock.Of<IAgentTemplateService>(),
            Mock.Of<IUserSettingsService>(),
            indexer.Object,
            NullLogger<KnowledgeStoreService>.Instance);
        store.Configuration.MaxTokensPerChunk++;

        await service.UpdateAsync(store);

        Assert.Equal(KnowledgeStoreIndexState.Outdated, store.Index.State);
        indexer.Verify(service => service.RequestRebuild(), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_AppliesResultLimitGloballyAcrossStores()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var first = CreateReadyStore("First", 2);
        var second = CreateReadyStore("Second", 2);
        await AddChunkAsync(vectors, first, new float[] { 1, 0 }, "first");
        await AddChunkAsync(vectors, second, new float[] { 0, 1 }, "second");
        var stores = new Mock<IKnowledgeStoreService>(MockBehavior.Strict);
        stores.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
        settings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UserSettings { Embedding = new EmbeddingSettings { RagMinRelevanceScore = -1 } });
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.Setup(service => service.GenerateEmbeddingAsync("query", It.IsAny<ServerModel>(), It.IsAny<CancellationToken>())).ReturnsAsync([1f, 0f]);
        var service = new KnowledgeSearchService(stores.Object, settings.Object, ollama.Object, vectors);

        var response = await service.SearchAsync([first.Id, second.Id], "query", maxResults: 1);

        var result = Assert.Single(response.Results);
        Assert.Equal("First", result.KnowledgeStoreName);
        Assert.Equal("first", result.Content);
    }

    private static KnowledgeStore CreateReadyStore(string name, int dimensions)
    {
        var configuration = new KnowledgeStoreIndexConfiguration { ServerId = Guid.NewGuid(), Model = "embedding", Dimensions = dimensions };
        return new KnowledgeStore
        {
            Name = name,
            Configuration = configuration.Clone(),
            Index = new KnowledgeStoreIndexMetadata { State = KnowledgeStoreIndexState.Ready, IndexedConfiguration = configuration.Clone() },
            Documents = [new KnowledgeDocument { FileName = "notes.md" }]
        };
    }

    private static async Task AddChunkAsync(KnowledgeVectorStore vectors, KnowledgeStore store, float[] embedding, string content)
    {
        var documentId = Guid.NewGuid();
        await vectors.ReplaceDocumentAsync(store.Id, documentId, embedding.Length,
        [
            new KnowledgeChunkRecord
            {
                Id = $"{store.Id:N}:{documentId:N}:0", KnowledgeStoreId = store.Id.ToString("N"), DocumentId = documentId.ToString("N"),
                FileName = "notes.md", Content = content, Embedding = embedding
            }
        ], CancellationToken.None);
    }

    private sealed class TemporaryVectorDatabase : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OllamaChatTests", Guid.NewGuid().ToString("N"));

        public KnowledgeVectorStore CreateStore()
        {
            Directory.CreateDirectory(_directory);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["KnowledgeVectorStore:DatabasePath"] = Path.Combine(_directory, "knowledge.sqlite") })
                .Build();
            return new KnowledgeVectorStore(configuration);
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            return ValueTask.CompletedTask;
        }
    }
}
