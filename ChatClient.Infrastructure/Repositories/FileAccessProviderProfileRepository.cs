using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class FileAccessProviderProfileRepository : IFileAccessProviderProfileRepository
{
    private readonly JsonFileRepository<List<FileAccessProviderProfile>> _repository;

    public FileAccessProviderProfileRepository(IConfiguration configuration, ILogger<FileAccessProviderProfileRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["FileAccessProviderProfiles:FilePath"], FilePathConstants.DefaultFileAccessProviderProfilesFile);
        _repository = new JsonFileRepository<List<FileAccessProviderProfile>>(path, logger);
    }

    public async Task<IReadOnlyCollection<FileAccessProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<FileAccessProviderProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
