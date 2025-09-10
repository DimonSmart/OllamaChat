namespace ChatClient.Api.Services;

using ChatClient.Domain.Models;
using OllamaSharp;

public interface IOllamaClientService : IOllamaEmbeddingService
{
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId);
    Task<OllamaApiClient> GetClientAsync(Guid serverId);
}
