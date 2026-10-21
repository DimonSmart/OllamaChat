using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IScheduledTaskRepository
{
    Task<IReadOnlyList<ScheduledTaskDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TResult> UpdateAsync<TResult>(Func<List<ScheduledTaskDefinition>, TResult> mutation, CancellationToken cancellationToken = default);
}
