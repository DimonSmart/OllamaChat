using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class UserSettingsRepository : IUserSettingsRepository
{
    private readonly JsonFileRepository<UserSettings> _repo;

    public UserSettingsRepository(IConfiguration configuration, ILogger<UserSettingsRepository> logger)
    {
        var filePath = configuration["UserSettings:FilePath"] ?? FilePathConstants.DefaultUserSettingsFile;
        _repo = new JsonFileRepository<UserSettings>(filePath, logger);
    }

    public bool Exists => _repo.Exists;

    public Task<UserSettings?> GetAsync(CancellationToken cancellationToken = default) =>
        _repo.ReadAsync(cancellationToken);

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(settings, cancellationToken);
}

