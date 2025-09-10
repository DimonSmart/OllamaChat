using ChatClient.Application.Repositories;

namespace ChatClient.Infrastructure.Repositories;

public class RagVectorIndexRepository : IRagVectorIndexRepository
{
    public bool SourceExists(string path) => File.Exists(path);

    public Task<string> ReadSourceAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public DateTime GetSourceModifiedUtc(string path) => new FileInfo(path).LastWriteTimeUtc;

    public async Task WriteIndexAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}
