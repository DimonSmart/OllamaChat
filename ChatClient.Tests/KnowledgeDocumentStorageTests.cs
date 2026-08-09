using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Tests;

public sealed class KnowledgeDocumentStorageTests
{
    [Fact]
    public async Task OpenSourceReadAsync_ReturnsOriginalSourceAfterWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "OllamaChatTests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
            var storage = new KnowledgeDocumentStorage(configuration);
            var storeId = Guid.NewGuid();
            var documentId = Guid.NewGuid();
            await using var source = new MemoryStream("original source"u8.ToArray());

            await storage.WriteAsync(storeId, documentId, "notes.txt", source, "canonical", TestContext.Current.CancellationToken);
            await using var reopened = await storage.OpenSourceReadAsync(storeId, documentId, TestContext.Current.CancellationToken);

            Assert.NotNull(reopened);
            using var reader = new StreamReader(reopened);
            Assert.Equal("original source", await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
