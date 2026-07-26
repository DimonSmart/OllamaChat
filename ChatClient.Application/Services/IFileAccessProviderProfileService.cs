using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IFileAccessProviderProfileService
{
    Task<IReadOnlyCollection<FileAccessProviderProfile>> GetAllAsync();
    Task<FileAccessProviderProfile?> GetByIdAsync(Guid id);
    Task CreateAsync(FileAccessProviderProfile profile);
    Task UpdateAsync(FileAccessProviderProfile profile);
    Task DeleteAsync(Guid id);
}
