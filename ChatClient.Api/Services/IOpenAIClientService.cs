using ChatClient.Domain.Models;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public interface IOpenAIClientService
{
    Task<IChatCompletionService> GetClientAsync(ServerModel serverModel, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default);
}
