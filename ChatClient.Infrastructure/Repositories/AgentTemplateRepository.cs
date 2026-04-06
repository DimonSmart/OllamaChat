using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class AgentTemplateRepository : IAgentTemplateRepository
{
    private readonly JsonFileRepository<List<AgentTemplateDefinition>> _repo;

    public AgentTemplateRepository(IConfiguration configuration, ILogger<AgentTemplateRepository> logger)
    {
        var filePath = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["AgentTemplates:FilePath"],
            FilePathConstants.DefaultAgentTemplatesFile);
        _repo = new JsonFileRepository<List<AgentTemplateDefinition>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repo.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<AgentTemplateDefinition> templates, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(templates, cancellationToken);
}

