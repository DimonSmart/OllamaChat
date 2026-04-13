namespace ChatClient.Api.Services;

using ChatClient.Domain.Models;
using OllamaSharp;

public interface IOllamaClientService
{
    Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default);
    bool EmbeddingsAvailable { get; }
    Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId);
    Task<OllamaApiClient> GetClientAsync(Guid serverId);
}
