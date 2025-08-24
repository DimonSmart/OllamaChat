using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;

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

    public event Func<Task>? EmbeddingModelChanged;

    public UserSettingsService(IConfiguration configuration, ILogger<UserSettingsService> logger)
    {
        _logger = logger;

        var settingsDir = configuration["UserSettings:Directory"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
        if (!Directory.Exists(settingsDir))
            Directory.CreateDirectory(settingsDir);

        _settingsFilePath = Path.Combine(settingsDir, "user_settings.json");
        _logger.LogInformation("User settings file path: {FilePath}", _settingsFilePath);
    }

    public async Task<UserSettings> GetSettingsAsync()
    {
        if (!File.Exists(_settingsFilePath))
        {
            _logger.LogInformation("Settings file not found. Creating a new one with default settings");
            var defaultSettings = new UserSettings();
            await SaveSettingsAsync(defaultSettings);
            return defaultSettings;
        }

        var json = await File.ReadAllTextAsync(_settingsFilePath);
        return JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();
    }

    public async Task SaveSettingsAsync(UserSettings settings)
    {
        try
        {
            UserSettings? existing = null;
            if (File.Exists(_settingsFilePath))
            {
                var jsonExisting = await File.ReadAllTextAsync(_settingsFilePath);
                existing = JsonSerializer.Deserialize<UserSettings>(jsonExisting, _jsonOptions);
            }

            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, json);
            _logger.LogInformation("User settings saved successfully");

            if (existing != null && !string.Equals(existing.EmbeddingModelName, settings.EmbeddingModelName, StringComparison.OrdinalIgnoreCase))
            {
                if (EmbeddingModelChanged != null)
                    await Task.WhenAll(EmbeddingModelChanged.GetInvocationList().Cast<Func<Task>>().Select(d => d()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user settings");
        }
    }


}
