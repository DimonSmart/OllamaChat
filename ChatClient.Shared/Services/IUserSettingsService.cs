using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IUserSettingsService
{
    /// <summary>
    /// Gets the current user settings
    /// </summary>
    Task<UserSettings> GetSettingsAsync();

    /// <summary>
    /// Saves the user settings
    /// </summary>
    Task SaveSettingsAsync(UserSettings settings);
}
