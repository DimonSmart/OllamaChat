namespace ChatClient.Api.Services;

using ChatClient.Shared.Models;

public interface IOllamaEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default);
    bool EmbeddingsAvailable { get; }
}
