using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Options;
using System.Text;

namespace ChatClient.Api.Services.Rag;

public sealed class KnowledgeDocumentIngestionService(IOptions<KnowledgeIngestionOptions> options) : IKnowledgeDocumentIngestionService
{
    public async Task<PreparedKnowledgeDocument> PrepareAsync(string fileName, Stream source, string? contentType = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        await using var copy = new MemoryStream();
        await source.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        var extension = Path.GetExtension(fileName);
        var markdown = IsDirectFormat(extension)
            ? Encoding.UTF8.GetString(bytes)
            : await ConvertWithMarkItDownAsync(fileName, bytes, contentType, cancellationToken);
        return new PreparedKnowledgeDocument(bytes, NormalizeMarkdown(markdown));
    }

    public async Task<IngestionDocument> ReadCanonicalMarkdownAsync(string fileName, Stream markdown, CancellationToken cancellationToken = default) =>
        await new MarkdownReader().ReadAsync(markdown, fileName, "text/markdown", cancellationToken);

    private async Task<string> ConvertWithMarkItDownAsync(string fileName, byte[] source, string? contentType, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.Value.MarkItDownMcpEndpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("MarkItDown is not configured.");

        try
        {
            await using var stream = new MemoryStream(source, writable: false);
            var document = await new MarkItDownMcpReader(endpoint).ReadAsync(
                stream,
                fileName,
                ResolveMediaType(fileName, contentType),
                cancellationToken);
            return GetMarkdown(document);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"MarkItDown conversion failed for '{fileName}'.", exception);
        }
    }

    private static bool IsDirectFormat(string extension) =>
        extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);

    private static string GetMarkdown(IngestionDocument document) =>
        string.Join("\n", document.Sections.Select(section => section.GetMarkdown()));

    private static string ResolveMediaType(string fileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return contentType;

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeMarkdown(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Canonical Markdown is empty.");
        return normalized + "\n";
    }
}
