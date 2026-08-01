using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class ModelRuntimeLimitsRepository : IModelRuntimeLimitsRepository
{
    private readonly JsonFileRepository<List<ModelRuntimeLimits>> _repository;

    public ModelRuntimeLimitsRepository(IConfiguration configuration, ILogger<ModelRuntimeLimitsRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["ModelRuntimeLimits:FilePath"],
            FilePathConstants.DefaultModelRuntimeLimitsFile);
        _repository = new JsonFileRepository<List<ModelRuntimeLimits>>(path, logger);
    }

    public async Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<ModelRuntimeLimits> limits, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(limits, cancellationToken);
}
