namespace ChatClient.Api.Services;

using ChatClient.Domain.Models;

public interface IOllamaEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default);
    bool EmbeddingsAvailable { get; }
}
