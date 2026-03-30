using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IWorkflowDefinitionRepository
{
    Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(List<SavedWorkflowDefinition> workflows, CancellationToken cancellationToken = default);
}
