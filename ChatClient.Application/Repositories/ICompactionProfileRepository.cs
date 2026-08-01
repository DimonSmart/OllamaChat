using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface ICompactionProfileRepository
{
    Task<IReadOnlyCollection<CompactionProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(List<CompactionProfile> profiles, CancellationToken cancellationToken = default);
}
