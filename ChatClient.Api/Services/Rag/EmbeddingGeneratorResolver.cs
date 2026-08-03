using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Services.Rag;

public sealed class EmbeddingGeneratorResolver(
    ILlmServerConfigService servers,
    IOllamaClientService ollama,
    ILogger<EmbeddingGeneratorResolver> logger) : IEmbeddingGeneratorResolver
{
    public async Task<IEmbeddingGenerator<string, Embedding<float>>> ResolveAsync(
        ServerModel model,
        CancellationToken cancellationToken = default)
    {
        var server = await servers.GetByIdAsync(model.ServerId)
            ?? throw new InvalidOperationException($"Embedding server '{model.ServerId}' was not found for model '{model.ModelName}'.");
        if (server.ServerType != ServerType.Ollama)
            throw new NotSupportedException($"Embedding generation is not supported for server type '{server.ServerType}'. Server '{model.ServerId}', model '{model.ModelName}'.");

        logger.LogInformation("Resolved Ollama embedding generator for server {ServerId} and model {ModelName}", model.ServerId, model.ModelName);
        return new OllamaEmbeddingGenerator(ollama, model);
    }
}
