namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IUserSettingsRepository
{
    bool Exists { get; }
    Task<UserSettings?> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}

