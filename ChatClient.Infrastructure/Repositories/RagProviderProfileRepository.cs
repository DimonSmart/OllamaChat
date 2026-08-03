using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class RagProviderProfileRepository : IRagProviderProfileRepository
{
    private readonly JsonFileRepository<List<RagProviderProfile>> _repository;

    public RagProviderProfileRepository(IConfiguration configuration, ILogger<RagProviderProfileRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["RagProviderProfiles:FilePath"], FilePathConstants.DefaultRagProviderProfilesFile);
        _repository = new JsonFileRepository<List<RagProviderProfile>>(path, logger);
    }

    public async Task<IReadOnlyCollection<RagProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<RagProviderProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
