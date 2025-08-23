namespace ChatClient.Api.Services;

public interface IOllamaEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string input, string modelId, CancellationToken cancellationToken = default);
    bool EmbeddingsAvailable { get; }
}
