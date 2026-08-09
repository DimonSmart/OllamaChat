using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace ChatClient.Infrastructure.Repositories;

public sealed class AgentSkillsProfileRepository : IAgentSkillsProfileRepository
{
    private readonly JsonFileRepository<List<AgentSkillsProfile>> repository;
    public AgentSkillsProfileRepository(IConfiguration configuration, ILogger<AgentSkillsProfileRepository> logger) => repository = new(StoragePathResolver.ResolveUserPath(configuration, configuration["AgentSkillsProfiles:FilePath"], FilePathConstants.DefaultAgentSkillsProfilesFile), logger);
    public async Task<IReadOnlyCollection<AgentSkillsProfile>> GetAllAsync(CancellationToken cancellationToken = default) => await repository.ReadAsync(cancellationToken) ?? [];
    public Task SaveAllAsync(List<AgentSkillsProfile> profiles, CancellationToken cancellationToken = default) => repository.WriteAsync(profiles, cancellationToken);
}
