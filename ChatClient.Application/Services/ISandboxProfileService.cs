using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ISandboxProfileService
{
    Task<IReadOnlyCollection<SandboxProfile>> GetAllAsync();

    Task<SandboxProfile?> GetByIdAsync(Guid id);

    Task CreateAsync(SandboxProfile profile);

    Task UpdateAsync(SandboxProfile profile);

    Task DeleteAsync(Guid id);
}
