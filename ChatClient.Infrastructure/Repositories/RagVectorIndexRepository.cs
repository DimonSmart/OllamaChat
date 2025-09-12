using ChatClient.Application.Repositories;

namespace ChatClient.Infrastructure.Repositories;

public class RagVectorIndexRepository : IRagVectorIndexRepository
{
    public bool SourceExists(string path) => File.Exists(path);

    public Task<string> ReadSourceAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);
}
