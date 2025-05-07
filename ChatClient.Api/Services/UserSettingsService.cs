using System.Text.Json;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class UserSettingsService : IUserSettingsService
{
    private readonly string _settingsFilePath;
    private readonly ILogger<UserSettingsService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserSettingsService(IConfiguration configuration, ILogger<UserSettingsService> logger)
    {
        _logger = logger;
        
        // Create a directory for user settings if it doesn't exist
        var settingsDir = configuration["UserSettings:Directory"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
        if (!Directory.Exists(settingsDir))
        {
            Directory.CreateDirectory(settingsDir);
        }
        
        _settingsFilePath = Path.Combine(settingsDir, "user_settings.json");
        _logger.LogInformation("User settings file path: {FilePath}", _settingsFilePath);
    }
    
    public async Task<UserSettings> GetSettingsAsync()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                _logger.LogInformation("Settings file not found. Creating a new one with default settings");
                var defaultSettings = new UserSettings();
                await SaveSettingsAsync(defaultSettings);
                return defaultSettings;
            }

            var json = await File.ReadAllTextAsync(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions);
            
            return settings ?? new UserSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading user settings");
            return new UserSettings();
        }
    }
    
    public async Task SaveSettingsAsync(UserSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, json);
            _logger.LogInformation("User settings saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user settings");
        }
    }
}
