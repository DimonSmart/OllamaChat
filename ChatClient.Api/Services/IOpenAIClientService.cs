using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public interface IOpenAIClientService
{
    Task<IReadOnlyCollection<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default);
}
