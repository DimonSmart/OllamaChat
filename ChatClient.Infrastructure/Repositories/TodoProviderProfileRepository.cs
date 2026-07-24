using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class TodoProviderProfileRepository : ITodoProviderProfileRepository
{
    private readonly JsonFileRepository<List<TodoProviderProfile>> _repository;

    public TodoProviderProfileRepository(IConfiguration configuration, ILogger<TodoProviderProfileRepository> logger)
    {
        var filePath = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["TodoProviderProfiles:FilePath"],
            FilePathConstants.DefaultTodoProviderProfilesFile);
        _repository = new JsonFileRepository<List<TodoProviderProfile>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<TodoProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repository.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<TodoProviderProfile> profiles, CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(profiles, cancellationToken);
}
