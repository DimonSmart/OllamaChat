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
    public async Task EmbeddingResolver_ReturnsOllamaAdapterAndPreservesProviderEmbedding()
    {
        var serverId = Guid.NewGuid();
        var servers = new Mock<ILlmServerConfigService>(MockBehavior.Strict);
        servers.Setup(service => service.GetByIdAsync(serverId)).ReturnsAsync(new LlmServerConfig { Id = serverId, ServerType = ServerType.Ollama });
        var ollama = new Mock<IOllamaClientService>(MockBehavior.Strict);
        ollama.Setup(service => service.GenerateEmbeddingAsync("query", It.IsAny<ServerModel>(), It.IsAny<CancellationToken>())).ReturnsAsync([1f, 2f]);
        var resolver = new EmbeddingGeneratorResolver(servers.Object, ollama.Object, NullLogger<EmbeddingGeneratorResolver>.Instance);

        var generator = await resolver.ResolveAsync(new ServerModel(serverId, "embedding"), cancellationToken: TestContext.Current.CancellationToken);
        var embeddings = await generator.GenerateAsync(["query"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new float[] { 1f, 2f }, embeddings[0].Vector.ToArray());
    }

    [Fact]
    public async Task EmbeddingResolver_RejectsUnsupportedServerType()
    {
        var serverId = Guid.NewGuid();
        var servers = new Mock<ILlmServerConfigService>(MockBehavior.Strict);
        servers.Setup(service => service.GetByIdAsync(serverId)).ReturnsAsync(new LlmServerConfig { Id = serverId, ServerType = ServerType.ChatGpt });
        var resolver = new EmbeddingGeneratorResolver(servers.Object, Mock.Of<IOllamaClientService>(), NullLogger<EmbeddingGeneratorResolver>.Instance);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.ResolveAsync(new ServerModel(serverId, "embedding"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("ChatGpt", error.Message);
    }

    [Fact]
    public async Task SearchAsync_DimensionMismatchPreservesExistingVectors()
    {
        await using var database = new TemporaryVectorDatabase();
        IKnowledgeIndex index = database.CreateStore();
        var store = CreateReadyStore("Knowledge", 2);
        var documentId = Guid.NewGuid();
        await AddChunkAsync(index, store, documentId, [1f, 0f], "preserved");

        await Assert.ThrowsAsync<InvalidOperationException>(() => SearchAsync(index, store, [1f, 0f, 0f], 5, -1));

        var results = await SearchAsync(index, store, [1f, 0f], 5, -1);
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

        await service.UpdateAsync(store, TestContext.Current.CancellationToken);

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

        await service.RequestReindexAsync(store.Id, TestContext.Current.CancellationToken);

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

        await service.UpdateAsync(store, TestContext.Current.CancellationToken);

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
    public async Task SearchAsync_UsesOneEmbeddingPerConfigurationAndAppliesGlobalLimit()
    {
        var first = CreateReadyStore("First", 2);
        var second = CreateReadyStore("Second", 2);
        second.Configuration.ServerId = first.Configuration.ServerId;
        second.Index.IndexedConfiguration = second.Configuration.Clone();
        var stores = new Mock<IKnowledgeStoreService>(MockBehavior.Strict);
        stores.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([first, second]);
        var settings = Mock.Of<IUserSettingsService>();
        var generator = new Mock<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(MockBehavior.Strict);
        generator.Setup(service => service.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<Microsoft.Extensions.AI.EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>([new Microsoft.Extensions.AI.Embedding<float>(new float[] { 1f, 0f })]));
        var resolver = new Mock<IEmbeddingGeneratorResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(It.IsAny<ServerModel>(), It.IsAny<CancellationToken>())).ReturnsAsync(generator.Object);
        var index = new Mock<IKnowledgeIndex>(MockBehavior.Strict);
        index.Setup(service => service.SearchVectorAsync(It.IsAny<KnowledgeVectorSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeVectorSearchRequest request, CancellationToken _) => [new RagSearchResult { Content = request.Store.Name, Score = request.Store == first ? 1 : .5 }]);
        var service = new KnowledgeSearchService(stores.Object, settings, resolver.Object, index.Object);

        var response = await service.SearchAsync(new KnowledgeSearchRequest { KnowledgeStoreIds = [first.Id, second.Id], Query = "query", MaxResults = 1, MinVectorRelevanceScore = null }, TestContext.Current.CancellationToken);

        Assert.Equal("First", Assert.Single(response.Results).Content);
        resolver.Verify(service => service.ResolveAsync(It.IsAny<ServerModel>(), It.IsAny<CancellationToken>()), Times.Once);
        generator.Verify(service => service.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<Microsoft.Extensions.AI.EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()), Times.Once);
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

    private static async Task AddChunkAsync(IKnowledgeIndex index, KnowledgeStore store, Guid documentId, float[] embedding, string content)
    {
        await index.ReplaceDocumentAsync(new KnowledgeDocumentIndexBatch
        {
            KnowledgeStoreId = store.Id,
            DocumentId = documentId,
            EmbeddingDimension = embedding.Length,
            Chunks = [new KnowledgeIndexedChunk { Id = $"{store.Id:N}:{documentId:N}:0", KnowledgeStoreId = store.Id, DocumentId = documentId, FileName = "notes.md", ChunkIndex = 0, Content = content, Embedding = embedding }]
        });
    }

    private static Task<IReadOnlyList<RagSearchResult>> SearchAsync(IKnowledgeIndex index, KnowledgeStore store, float[] embedding, int max, double? threshold) =>
        index.SearchVectorAsync(new KnowledgeVectorSearchRequest { Store = store, QueryEmbedding = embedding, MaxResults = max, MinRelevanceScore = threshold });

    private sealed class TemporaryVectorDatabase : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OllamaChatTests", Guid.NewGuid().ToString("N"));

        public SqliteKnowledgeIndex CreateStore()
        {
            Directory.CreateDirectory(_directory);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["KnowledgeVectorStore:DatabasePath"] = Path.Combine(_directory, "knowledge.sqlite") })
                .Build();
            return new SqliteKnowledgeIndex(configuration);
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
