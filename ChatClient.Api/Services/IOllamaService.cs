namespace ChatClient.Api.Services;

using ChatClient.Shared.Models;

public interface IOllamaService : IOllamaEmbeddingService
{
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync();
}
