using ChatClient.Application.Services;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Infrastructure.Repositories;

public sealed class KnowledgeDocumentStorage(IConfiguration configuration) : IKnowledgeDocumentStorage
{
    private readonly string _root = StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeStores:DocumentsPath"], "UserData/knowledge-stores");
    public Task<string?> ReadCanonicalMarkdownAsync(Guid storeId, Guid documentId, CancellationToken ct = default)
    {
        var path = MarkdownPathFor(storeId, documentId);
        return File.Exists(path) ? File.ReadAllTextAsync(path, ct).ContinueWith(x => (string?)x.Result, ct) : Task.FromResult<string?>(null);
    }
    public async Task WriteAsync(Guid storeId, Guid documentId, string fileName, Stream source, string canonicalMarkdown, CancellationToken ct = default)
    {
        var directory = DocumentDirectory(storeId, documentId);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(fileName);
        var sourcePath = Path.Combine(directory, "source" + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension));
        await using (var destination = File.Create(sourcePath))
            await source.CopyToAsync(destination, ct);
        await File.WriteAllTextAsync(MarkdownPathFor(storeId, documentId), canonicalMarkdown, ct);
    }
    public Task DeleteAsync(Guid storeId, Guid documentId, CancellationToken ct = default) { var path = DocumentDirectory(storeId, documentId); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
    public Task DeleteStoreAsync(Guid storeId, CancellationToken ct = default) { var path = Path.Combine(_root, storeId.ToString("N")); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
    private string DocumentDirectory(Guid storeId, Guid documentId) => Path.Combine(_root, storeId.ToString("N"), "documents", documentId.ToString("N"));
    private string MarkdownPathFor(Guid storeId, Guid documentId) => Path.Combine(DocumentDirectory(storeId, documentId), "content.md");
}
