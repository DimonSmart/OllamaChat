using ChatClient.Api.Services.Rag;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
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

    [Fact]
    public async Task MarkdownReaderAndHeaderChunker_PreserveHeaderContext()
    {
        var service = CreateService();
        await using var markdown = new MemoryStream(Encoding.UTF8.GetBytes("# First\n\nAAA\n\n## Second\n\nBBB\n"));
        var document = await service.ReadCanonicalMarkdownAsync("notes.md", markdown);
        var chunker = new HeaderChunker(new IngestionChunkerOptions(TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null))
        {
            MaxTokensPerChunk = 100,
            OverlapTokens = 0
        });
        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(document))
            chunks.Add(chunk);

        Assert.Contains(chunks, chunk => chunk.Context?.Contains("First", StringComparison.Ordinal) == true);
        Assert.Contains(chunks, chunk => chunk.Context?.Contains("Second", StringComparison.Ordinal) == true);
    }

    private static KnowledgeDocumentIngestionService CreateService() =>
        new(Options.Create(new KnowledgeIngestionOptions()));
}
