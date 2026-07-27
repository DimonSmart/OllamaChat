using ChatClient.Api.Services.Rag;
using System.Text;

namespace ChatClient.Tests;

public sealed class KnowledgeDocumentIngestionServiceTests
{
    [Fact]
    public async Task PrepareAsync_NormalizesDirectMarkdown()
    {
        var service = CreateService();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("\r\n# Title\r\n\r\nText\r\n\r\n"));

        var prepared = await service.PrepareAsync("notes.md", source);

        Assert.Equal("# Title\n\nText\n", prepared.CanonicalMarkdown);
    }

    [Fact]
    public async Task PrepareAsync_WrapsMarkItDownConversionFailure()
    {
        var service = new KnowledgeDocumentIngestionService(new FailingConverter());
        await using var source = new MemoryStream([1, 2, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("book.pdf", source));

        Assert.Equal("MarkItDown conversion failed for 'book.pdf'.", exception.Message);
    }

    private static KnowledgeDocumentIngestionService CreateService() =>
        new(new FailingConverter());

    private sealed class FailingConverter : IDocumentMarkdownConverter
    {
        public Task<string> ConvertAsync(string fileName, Stream content, string? contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MarkItDown MCP stopped unexpectedly.");
    }
}
