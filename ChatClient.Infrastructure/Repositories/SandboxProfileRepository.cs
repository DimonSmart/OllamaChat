using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class SandboxProfileRepository : ISandboxProfileRepository
{
    private readonly JsonFileRepository<List<SandboxProfile>> _repository;

    public SandboxProfileRepository(IConfiguration configuration, ILogger<SandboxProfileRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["SandboxProfiles:FilePath"],
            FilePathConstants.DefaultSandboxProfilesFile);
        _repository = new JsonFileRepository<List<SandboxProfile>>(path, logger);
    }

    public async Task<IReadOnlyCollection<SandboxProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<SandboxProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
