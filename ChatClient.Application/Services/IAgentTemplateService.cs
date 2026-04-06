using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IAgentTemplateService
{
    Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync();
    Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId);
    Task CreateAsync(AgentTemplateDefinition template);
    Task UpdateAsync(AgentTemplateDefinition template);
    Task DeleteAsync(Guid templateId);
}
