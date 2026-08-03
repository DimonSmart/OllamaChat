using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Application.Services;

public interface IEmbeddingGeneratorResolver
{
    Task<IEmbeddingGenerator<string, Embedding<float>>> ResolveAsync(
        ServerModel model,
        CancellationToken cancellationToken = default);
}
