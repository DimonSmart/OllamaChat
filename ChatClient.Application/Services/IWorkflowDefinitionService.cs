using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IWorkflowDefinitionService
{
    Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync();

    Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId);

    Task CreateAsync(SavedWorkflowDefinition workflow);

    Task UpdateAsync(SavedWorkflowDefinition workflow);

    Task DeleteAsync(Guid workflowId);
}
