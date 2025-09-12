namespace ChatClient.Application.Repositories;

public interface IRagVectorIndexRepository
{
    bool SourceExists(string path);
    Task<string> ReadSourceAsync(string path, CancellationToken cancellationToken = default);
}
