using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class WorkflowDefinitionRepository : IWorkflowDefinitionRepository
{
    private readonly JsonFileRepository<List<SavedWorkflowDefinition>> _repo;

    public WorkflowDefinitionRepository(IConfiguration configuration, ILogger<WorkflowDefinitionRepository> logger)
    {
        var filePath = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["WorkflowDefinitions:FilePath"],
            FilePathConstants.DefaultWorkflowDefinitionsFile);
        _repo = new JsonFileRepository<List<SavedWorkflowDefinition>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repo.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<SavedWorkflowDefinition> workflows, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(workflows, cancellationToken);
}
