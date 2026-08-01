using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ICompactionProfileService
{
    Task<IReadOnlyCollection<CompactionProfile>> GetAllAsync();

    Task<CompactionProfile?> GetByIdAsync(Guid id);

    Task CreateAsync(CompactionProfile profile);

    Task UpdateAsync(CompactionProfile profile);

    Task DeleteAsync(Guid id);

    Task RestoreBuiltInAsync();
}
