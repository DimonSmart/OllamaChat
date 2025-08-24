namespace ChatClient.Api.Services;

using ChatClient.Shared.Models;

public interface IOllamaClientService : IOllamaEmbeddingService
{
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid? serverId = null);
}
