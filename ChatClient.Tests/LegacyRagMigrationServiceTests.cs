using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public sealed class LegacyRagMigrationServiceTests
{
    [Fact]
    public async Task MigrateAsync_IsIdempotentAndImportsLegacyTextWithoutIngestion()
    {
        await using var fixture = new MigrationFixture();
        var agent = new AgentTemplateDefinition { Id = Guid.NewGuid(), AgentName = "Agent A", Content = "Test" };
        await fixture.Agents.CreateAsync(agent);
        var legacyDirectory = Path.Combine(fixture.Root, "UserData", "agents", agent.Id.ToString(), "files");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "a.txt"), "first\r\ntext");
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "manual.pdf"), "legacy PDF extraction\r\ncontent");

        await fixture.Service.MigrateAsync();
        await fixture.Service.MigrateAsync();

        var store = Assert.Single(await fixture.Stores.GetAllAsync());
        Assert.Equal(agent.Id, store.Id);
        Assert.Equal("Agent A - Migrated Knowledge", store.Name);
        Assert.Equal(2, store.Documents.Count);
        var manual = Assert.Single(store.Documents, document => document.FileName == "manual.pdf");
        Assert.Equal("text/plain", manual.ContentType);
        Assert.Equal("legacy PDF extraction\ncontent\n", await fixture.Documents.ReadCanonicalMarkdownAsync(store.Id, manual.Id));
        Assert.Equal("legacy PDF extraction\ncontent\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "UserData", "knowledge-stores", store.Id.ToString("N"), "documents", manual.Id.ToString("N"), "source.legacy.txt")));
        var migratedAgent = await fixture.Agents.GetByIdAsync(agent.Id);
        Assert.Contains(store.Id, migratedAgent!.KnowledgeStoreIds);
        fixture.Indexer.Verify(service => service.RequestRebuild(), Times.Once);
    }

    [Fact]
    public async Task DeleteAgent_LeavesSharedKnowledgeStoreAvailableToOtherAgent()
    {
        await using var fixture = new MigrationFixture();
        var store = new KnowledgeStore { Name = "Shared" };
        await fixture.Stores.SaveAsync(store);
        var first = new AgentTemplateDefinition { AgentName = "A", Content = "Test", KnowledgeStoreIds = [store.Id] };
        var second = new AgentTemplateDefinition { AgentName = "B", Content = "Test", KnowledgeStoreIds = [store.Id] };
        await fixture.Agents.CreateAsync(first);
        await fixture.Agents.CreateAsync(second);

        await fixture.Agents.DeleteAsync(first.Id);

        var remainingStore = (await fixture.Stores.GetAllAsync()).SingleOrDefault(item => item.Id == store.Id);
        Assert.NotNull(remainingStore);
        var remainingAgent = await fixture.Agents.GetByIdAsync(second.Id);
        Assert.Contains(store.Id, remainingAgent!.KnowledgeStoreIds);
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OllamaChatTests", Guid.NewGuid().ToString("N"));
        public IKnowledgeStoreRepository Stores { get; }
        public IKnowledgeDocumentStorage Documents { get; }
        public AgentTemplateService Agents { get; }
        public Mock<IKnowledgeIndexBackgroundService> Indexer { get; } = new(MockBehavior.Strict);
        public LegacyRagMigrationService Service { get; }

        public MigrationFixture()
        {
            Directory.CreateDirectory(Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = Root }).Build();
            Stores = new KnowledgeStoreRepository(configuration, NullLogger<KnowledgeStoreRepository>.Instance);
            Documents = new KnowledgeDocumentStorage(configuration);
            Agents = new AgentTemplateService(new AgentTemplateRepository(configuration, NullLogger<AgentTemplateRepository>.Instance));
            Indexer.Setup(service => service.RequestRebuild());
            var settings = new Mock<IUserSettingsService>(MockBehavior.Strict);
            settings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UserSettings
            {
                Embedding = new EmbeddingSettings { Model = new ServerModelSelection(Guid.NewGuid(), "embedding") }
            });
            Service = new LegacyRagMigrationService(configuration, Stores, Documents, Agents, settings.Object, Indexer.Object, NullLogger<LegacyRagMigrationService>.Instance);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
