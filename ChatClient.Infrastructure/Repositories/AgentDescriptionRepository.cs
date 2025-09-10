using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class AgentDescriptionRepository : IAgentDescriptionRepository
{
    private readonly JsonFileRepository<List<AgentDescription>> _repo;

    public AgentDescriptionRepository(IConfiguration configuration, ILogger<AgentDescriptionRepository> logger)
    {
        var filePath = configuration["AgentDescriptions:FilePath"] ?? FilePathConstants.DefaultAgentDescriptionsFile;
        _repo = new JsonFileRepository<List<AgentDescription>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<AgentDescription>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repo.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<AgentDescription> agents, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(agents, cancellationToken);
}

