using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IRagVectorIndexService
{
    Task BuildIndexAsync(Guid agentId, string sourceFilePath, string indexFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default);
}
