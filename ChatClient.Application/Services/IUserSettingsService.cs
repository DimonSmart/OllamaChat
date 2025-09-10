using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IUserSettingsService
{
    Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
