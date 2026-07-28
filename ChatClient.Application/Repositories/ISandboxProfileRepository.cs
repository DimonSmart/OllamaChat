using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface ISandboxProfileRepository
{
    Task<IReadOnlyCollection<SandboxProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(List<SandboxProfile> profiles, CancellationToken cancellationToken = default);
}
