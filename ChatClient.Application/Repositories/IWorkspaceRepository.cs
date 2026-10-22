using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IWorkspaceRepository
{
    Task<IReadOnlyList<WorkspaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TResult> UpdateAsync<TResult>(
        Func<List<WorkspaceDefinition>, TResult> mutation, CancellationToken cancellationToken = default);
}
