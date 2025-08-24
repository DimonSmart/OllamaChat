namespace ChatClient.Api.Services;

public interface IOllamaEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string input, string modelId, Guid? serverId = null, CancellationToken cancellationToken = default);
    bool EmbeddingsAvailable { get; }
}
