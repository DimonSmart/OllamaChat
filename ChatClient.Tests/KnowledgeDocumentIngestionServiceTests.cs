using ChatClient.Api.Services.Rag;
using Microsoft.Extensions.Options;
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
    public async Task PrepareAsync_RejectsComplexDocumentWhenMarkItDownIsNotConfigured()
    {
        var service = CreateService();
        await using var source = new MemoryStream([1, 2, 3]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync("book.pdf", source));

        Assert.Equal("MarkItDown is not configured.", exception.Message);
    }

    private static KnowledgeDocumentIngestionService CreateService() =>
        new(Options.Create(new KnowledgeIngestionOptions()));
}
