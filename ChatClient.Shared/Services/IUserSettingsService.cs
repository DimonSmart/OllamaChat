using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IUserSettingsService
{
    Task<UserSettings> GetSettingsAsync();
    Task SaveSettingsAsync(UserSettings settings);
}
