using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Services.Rag;

public sealed class OllamaEmbeddingGenerator(IOllamaClientService ollama, ServerModel model) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var embeddings = new List<Embedding<float>>();
        foreach (var value in values)
        {
            var embedding = await ollama.GenerateEmbeddingAsync(value, model, cancellationToken);
            if (embedding is not { Length: > 0 })
                throw new InvalidOperationException("Embedding provider returned an empty embedding.");
            embeddings.Add(new Embedding<float>(embedding));
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
