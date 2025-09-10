using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IRagVectorIndexService
{
    Task BuildIndexAsync(Guid agentId, string sourceFilePath, string indexFilePath, IProgress<RagVectorIndexStatus>? progress = null, CancellationToken cancellationToken = default, Guid serverId = default);
}
