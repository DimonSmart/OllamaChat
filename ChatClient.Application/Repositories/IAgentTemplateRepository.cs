namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IAgentTemplateRepository
{
    Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<AgentTemplateDefinition> templates, CancellationToken cancellationToken = default);
}

