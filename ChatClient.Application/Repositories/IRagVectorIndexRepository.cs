namespace ChatClient.Application.Repositories;

public interface IRagVectorIndexRepository
{
    bool SourceExists(string path);
    Task<string> ReadSourceAsync(string path, CancellationToken cancellationToken = default);
    DateTime GetSourceModifiedUtc(string path);
    Task WriteIndexAsync(string path, string content, CancellationToken cancellationToken = default);
}
