using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IFileAccessProviderProfileRepository
{
    Task<IReadOnlyCollection<FileAccessProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<FileAccessProviderProfile> profiles, CancellationToken cancellationToken = default);
}
