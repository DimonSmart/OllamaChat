using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class CompactionProfileRepository : ICompactionProfileRepository
{
    private readonly JsonFileRepository<List<CompactionProfile>> _repository;

    public CompactionProfileRepository(IConfiguration configuration, ILogger<CompactionProfileRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["CompactionProfiles:FilePath"],
            FilePathConstants.DefaultCompactionProfilesFile);
        _repository = new JsonFileRepository<List<CompactionProfile>>(path, logger);
    }

    public async Task<IReadOnlyCollection<CompactionProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<CompactionProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
