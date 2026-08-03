using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public sealed class KnowledgeStoreRegressionTests
{
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
    public async Task RequestReindexAsync_RequestsFullRebuildAndKeepsActiveIndexingState()
    {
        var store = CreateReadyStore("Knowledge", 2);
        store.Index.State = KnowledgeStoreIndexState.Indexing;
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

        await service.RequestReindexAsync(store.Id);

        Assert.True(store.Index.ForceRebuild);
        Assert.Equal(KnowledgeStoreIndexState.Indexing, store.Index.State);
        indexer.Verify(service => service.RequestRebuild(), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_FailedStoreDoesNotRequestAnotherRebuild()
    {
        var store = CreateReadyStore("Knowledge", 2);
        store.Index.State = KnowledgeStoreIndexState.Failed;
        var repository = new Mock<IKnowledgeStoreRepository>(MockBehavior.Strict);
        repository.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([store]);
        repository.Setup(service => service.SaveAsync(store, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var indexer = new Mock<IKnowledgeIndexBackgroundService>(MockBehavior.Strict);
        var service = new KnowledgeStoreService(
            repository.Object,
            Mock.Of<IKnowledgeDocumentStorage>(),
            Mock.Of<IKnowledgeDocumentIngestionService>(),
            Mock.Of<IAgentTemplateService>(),
            Mock.Of<IUserSettingsService>(),
            indexer.Object,
            NullLogger<KnowledgeStoreService>.Instance);

        await service.UpdateAsync(store);

        indexer.Verify(service => service.RequestRebuild(), Times.Never);
    }

    [Fact]
    public void NeedsIndexing_FailedStoreIsNotAutomaticallyRetried()
    {
        var store = CreateReadyStore("Knowledge", 2);
        store.Index.State = KnowledgeStoreIndexState.Failed;
        store.Documents[0].SourceHash = "current";
        store.Documents[0].IndexedSourceHash = "previous";

        Assert.False(KnowledgeIndexBackgroundService.NeedsIndexing(store));
    }

    [Fact]
    public void ApplyCurrentIngestionVersion_MarksLegacyStoreOutdated()
    {
        var store = CreateReadyStore("Knowledge", 2);
        store.Configuration.IngestionVersion = "markdown-header-v2";

        var changed = KnowledgeIndexBackgroundService.ApplyCurrentIngestionVersion(store);

        Assert.True(changed);
        Assert.Equal(KnowledgeStoreIndexConfiguration.CurrentIngestionVersion, store.Configuration.IngestionVersion);
        Assert.Equal(KnowledgeStoreIndexState.Outdated, store.Index.State);
    }

    [Fact]
    public void ApplyCurrentIngestionVersion_PreservesFailedState()
    {
        var store = CreateReadyStore("Knowledge", 2);
        store.Index.State = KnowledgeStoreIndexState.Failed;
        store.Configuration.IngestionVersion = "markdown-header-v2";

        var changed = KnowledgeIndexBackgroundService.ApplyCurrentIngestionVersion(store);

        Assert.True(changed);
        Assert.Equal(KnowledgeStoreIndexState.Failed, store.Index.State);
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

        var response = await service.SearchAsync(new KnowledgeSearchRequest { KnowledgeStoreIds = [first.Id, second.Id], Query = "query", MaxResults = 1, UseApplicationDefaultThreshold = true });

        var result = Assert.Single(response.Results);
        Assert.Equal("First", result.KnowledgeStoreName);
        Assert.Equal("first", result.Content);
    }

    [Fact]
    public async Task SearchAsync_DefaultOverloadAppliesConfiguredRelevanceThreshold()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        await AddChunkAsync(vectors, store, new float[] { 0.6f, 0.8f }, "weak");
        var stores = new Mock<IKnowledgeStoreService>(MockBehavior.Strict);
        stores.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([store]);
        var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
        settings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { Embedding = new EmbeddingSettings { RagMinRelevanceScore = 0.8 } });
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.Setup(service => service.GenerateEmbeddingAsync("query", It.IsAny<ServerModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1f, 0f]);
        var service = new KnowledgeSearchService(stores.Object, settings.Object, ollama.Object, vectors);

        var response = await service.SearchAsync(new KnowledgeSearchRequest { KnowledgeStoreIds = [store.Id], Query = "query", MaxResults = 5, UseApplicationDefaultThreshold = true });

        Assert.Empty(response.Results);
        settings.Verify(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_ExplicitNullThresholdReturnsWeakResultsBelowConfiguredThreshold()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        await AddChunkAsync(vectors, store, new float[] { 0.6f, 0.8f }, "weak");
        var stores = new Mock<IKnowledgeStoreService>(MockBehavior.Strict);
        stores.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([store]);
        var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.Setup(service => service.GenerateEmbeddingAsync("query", It.IsAny<ServerModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1f, 0f]);
        var service = new KnowledgeSearchService(stores.Object, settings.Object, ollama.Object, vectors);

        var response = await service.SearchAsync(new KnowledgeSearchRequest { KnowledgeStoreIds = [store.Id], Query = "query", MaxResults = 5, MinVectorRelevanceScore = null });

        var result = Assert.Single(response.Results);
        Assert.Equal("weak", result.Content);
        settings.Verify(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_NullThresholdStillAppliesGlobalResultLimitAcrossStores()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var first = CreateReadyStore("First", 2);
        var second = CreateReadyStore("Second", 2);
        await AddChunkAsync(vectors, first, new float[] { 1, 0 }, "first");
        await AddChunkAsync(vectors, second, new float[] { 0.6f, 0.8f }, "second");
        var stores = new Mock<IKnowledgeStoreService>(MockBehavior.Strict);
        stores.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.Setup(service => service.GenerateEmbeddingAsync("query", It.IsAny<ServerModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1f, 0f]);
        var service = new KnowledgeSearchService(stores.Object, settings.Object, ollama.Object, vectors);

        var response = await service.SearchAsync(new KnowledgeSearchRequest { KnowledgeStoreIds = [first.Id, second.Id], Query = "query", MaxResults = 1, MinVectorRelevanceScore = null });

        var result = Assert.Single(response.Results);
        Assert.Equal("First", result.KnowledgeStoreName);
        Assert.Equal("first", result.Content);
    }

    [Fact]
    public async Task ReplaceDocumentAsync_ConcurrentWritesCompleteWithoutDatabaseLock()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        var documents = Enumerable.Range(0, 12).Select(index => new KnowledgeDocument { Id = Guid.NewGuid(), FileName = $"{index}.md" }).ToList();

        await Task.WhenAll(documents.Select((document, index) => vectors.ReplaceDocumentAsync(store.Id, document.Id, 2,
        [
            new KnowledgeChunkRecord
            {
                Id = $"{store.Id:N}:{document.Id:N}:0", KnowledgeStoreId = store.Id.ToString("N"), DocumentId = document.Id.ToString("N"),
                FileName = document.FileName, Content = $"document {index}", Embedding = new float[] { 1, 0 }
            }
        ], CancellationToken.None)));

        var results = await vectors.SearchAsync(store, new float[] { 1, 0 }, 20, -1, CancellationToken.None);
        Assert.Equal(documents.Count, results.Count);
    }

    [Fact]
    public async Task ReplaceDocumentAsync_ReplacesExistingDocumentWithoutDatabaseLock()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        var documentId = Guid.NewGuid();
        var first = new KnowledgeChunkRecord
        {
            Id = $"{store.Id:N}:{documentId:N}:0",
            KnowledgeStoreId = store.Id.ToString("N"),
            DocumentId = documentId.ToString("N"),
            FileName = "notes.md",
            Content = "first",
            Embedding = new float[] { 1, 0 }
        };
        var replacement = new KnowledgeChunkRecord
        {
            Id = first.Id,
            KnowledgeStoreId = first.KnowledgeStoreId,
            DocumentId = first.DocumentId,
            FileName = first.FileName,
            Content = "replacement",
            Embedding = first.Embedding
        };

        await vectors.ReplaceDocumentAsync(store.Id, documentId, 2, [first], CancellationToken.None);
        await vectors.ReplaceDocumentAsync(store.Id, documentId, 2, [replacement], CancellationToken.None);

        var results = await vectors.SearchAsync(store, new float[] { 1, 0 }, 5, -1, CancellationToken.None);
        var result = Assert.Single(results);
        Assert.Equal("replacement", result.Content);
    }

    [Fact]
    public async Task VectorSearchAsync_NullThresholdDoesNotFilterWeakResults()
    {
        await using var database = new TemporaryVectorDatabase();
        var vectors = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        await AddChunkAsync(vectors, store, new float[] { 0.6f, 0.8f }, "weak");

        var results = await vectors.SearchAsync(store, new float[] { 1, 0 }, 5, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("weak", result.Content);
        Assert.InRange(result.Score, 0.59d, 0.61d);
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
