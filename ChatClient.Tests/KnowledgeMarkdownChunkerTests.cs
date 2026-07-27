using ChatClient.Api.Services.Rag;
using Microsoft.ML.Tokenizers;

namespace ChatClient.Tests;

public sealed class KnowledgeMarkdownChunkerTests
{
    private readonly KnowledgeMarkdownChunker _chunker = new();

    [Fact]
    public void Chunk_AcceptsTaskListsWithoutMarkdownAstParsing()
    {
        var chunks = _chunker.Chunk("tasks.md", "# Tasks\n\n- [x] First\n- [ ] Second\n", 64, 8);

        var chunk = Assert.Single(chunks);
        Assert.Equal("Tasks", chunk.Section);
        Assert.Contains("- [ ] Second", chunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_PreservesHeadingPath()
    {
        var chunks = _chunker.Chunk("notes.md", "# One\n\nText A\n\n## Two\n\nText B\n", 4, 0);

        Assert.Contains(chunks, chunk => chunk.Section == "One" && chunk.Content.Contains("Text A", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.Section == "One > Two" && chunk.Content.Contains("Text B", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunk_SplitsOversizedBlockWithinConfiguredLimit()
    {
        var markdown = string.Join(' ', Enumerable.Repeat("knowledge", 200));
        var chunks = _chunker.Chunk("large.md", markdown, 20, 3);
        var tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(tokenizer.CountTokens(chunk.Content) <= 20));
    }
}
