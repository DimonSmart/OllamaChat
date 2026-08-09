using ChatClient.Application.Services;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Infrastructure.Repositories;

public sealed class KnowledgeDocumentStorage(IConfiguration configuration) : IKnowledgeDocumentStorage
{
    private readonly string _root = StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeStores:DocumentsPath"], "UserData/knowledge-stores");
    public Task<string?> ReadCanonicalMarkdownAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var path = MarkdownPathFor(storeId, documentId);
        return File.Exists(path) ? File.ReadAllTextAsync(path, cancellationToken).ContinueWith(x => (string?)x.Result, cancellationToken) : Task.FromResult<string?>(null);
    }
    public Task<Stream?> OpenSourceReadAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var directory = DocumentDirectory(storeId, documentId);
        if (!Directory.Exists(directory))
            return Task.FromResult<Stream?>(null);

        var sourcePath = Directory.EnumerateFiles(directory, "source.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        Stream? stream = sourcePath is null ? null : new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }
    public async Task WriteAsync(Guid storeId, Guid documentId, string fileName, Stream source, string canonicalMarkdown, CancellationToken cancellationToken = default)
    {
        var directory = DocumentDirectory(storeId, documentId);
        Directory.CreateDirectory(directory);
        foreach (var existingSource in Directory.EnumerateFiles(directory, "source.*", SearchOption.TopDirectoryOnly))
            File.Delete(existingSource);
        var extension = Path.GetExtension(fileName);
        var sourcePath = Path.Combine(directory, "source" + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension));
        await using (var destination = File.Create(sourcePath))
            await source.CopyToAsync(destination, cancellationToken);
        await File.WriteAllTextAsync(MarkdownPathFor(storeId, documentId), canonicalMarkdown, cancellationToken);
    }
    public Task DeleteAsync(Guid storeId, Guid documentId, CancellationToken cancellationToken = default) { var path = DocumentDirectory(storeId, documentId); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
    public Task DeleteStoreAsync(Guid storeId, CancellationToken cancellationToken = default) { var path = Path.Combine(_root, storeId.ToString("N")); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
    private string DocumentDirectory(Guid storeId, Guid documentId) => Path.Combine(_root, storeId.ToString("N"), "documents", documentId.ToString("N"));
    private string MarkdownPathFor(Guid storeId, Guid documentId) => Path.Combine(DocumentDirectory(storeId, documentId), "content.md");
}
