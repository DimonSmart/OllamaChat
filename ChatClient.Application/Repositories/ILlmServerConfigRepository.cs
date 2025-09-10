namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface ILlmServerConfigRepository
{
    Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<LlmServerConfig> servers, CancellationToken cancellationToken = default);
}

