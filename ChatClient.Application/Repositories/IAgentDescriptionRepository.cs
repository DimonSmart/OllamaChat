namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IAgentDescriptionRepository
{
    Task<IReadOnlyCollection<AgentDescription>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<AgentDescription> agents, CancellationToken cancellationToken = default);
}

