using ChatClient.Application.Services;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Infrastructure.Repositories;

public sealed class KnowledgeDocumentStorage(IConfiguration configuration) : IKnowledgeDocumentStorage
{
    private readonly string _root = StoragePathResolver.ResolveUserPath(configuration, configuration["KnowledgeStores:DocumentsPath"], "UserData/knowledge-stores");
    public Task<string?> ReadAsync(Guid storeId, Guid documentId, CancellationToken ct = default)
    {
        var path = PathFor(storeId, documentId);
        return File.Exists(path) ? File.ReadAllTextAsync(path, ct).ContinueWith(x => (string?)x.Result, ct) : Task.FromResult<string?>(null);
    }
    public async Task WriteAsync(Guid storeId, Guid documentId, string content, CancellationToken ct = default)
    {
        var path = PathFor(storeId, documentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }
    public Task DeleteAsync(Guid storeId, Guid documentId, CancellationToken ct = default) { var path = PathFor(storeId, documentId); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    public Task DeleteStoreAsync(Guid storeId, CancellationToken ct = default) { var path = Path.Combine(_root, storeId.ToString("N")); if (Directory.Exists(path)) Directory.Delete(path, true); return Task.CompletedTask; }
    private string PathFor(Guid storeId, Guid documentId) => Path.Combine(_root, storeId.ToString("N"), "documents", documentId.ToString("N") + ".txt");
}
