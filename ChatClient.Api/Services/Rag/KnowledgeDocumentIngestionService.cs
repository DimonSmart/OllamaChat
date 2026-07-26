using Microsoft.Extensions.DataIngestion;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeDocumentIngestionService : IKnowledgeDocumentIngestionService
{
    public async Task<PreparedKnowledgeDocument> PrepareAsync(string fileName, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        await using var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"'{extension}' requires a configured MarkItDown MCP endpoint. Configure MarkItDown before uploading this document type.");

        var markdown = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(markdown))
            throw new ArgumentException("Document content is empty.", nameof(source));
        return new PreparedKnowledgeDocument(bytes, markdown + "\n");
    }

    public async Task<IngestionDocument> ReadCanonicalMarkdownAsync(string fileName, Stream markdown, CancellationToken cancellationToken = default) =>
        await new MarkdownReader().ReadAsync(markdown, fileName, "text/markdown", cancellationToken);
}
