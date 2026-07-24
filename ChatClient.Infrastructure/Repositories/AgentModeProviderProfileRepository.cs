using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class AgentModeProviderProfileRepository : IAgentModeProviderProfileRepository
{
    private readonly JsonFileRepository<List<AgentModeProviderProfile>> _repository;

    public AgentModeProviderProfileRepository(IConfiguration configuration, ILogger<AgentModeProviderProfileRepository> logger)
    {
        var filePath = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["AgentModeProviderProfiles:FilePath"],
            FilePathConstants.DefaultAgentModeProviderProfilesFile);
        _repository = new JsonFileRepository<List<AgentModeProviderProfile>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<AgentModeProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<AgentModeProviderProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
