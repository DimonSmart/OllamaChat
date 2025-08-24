namespace ChatClient.Api.Services;

using ChatClient.Shared.Models;
using OllamaSharp;

public interface IOllamaClientService : IOllamaEmbeddingService
{
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId);
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync(ServerModel serverModel);
    Task<OllamaApiClient> GetClientAsync(Guid serverId);
}
