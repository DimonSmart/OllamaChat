namespace ChatClient.Shared.Services;

public interface IRagVectorIndexService
{
    Task BuildIndexAsync(string sourceFilePath, string indexFilePath, CancellationToken cancellationToken = default);
}
