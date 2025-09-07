using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IUserSettingsService
{
    Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
