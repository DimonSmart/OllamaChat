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
        var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();

        return await MigrateSettingsAsync(settings);
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

    private async Task<UserSettings> MigrateSettingsAsync(UserSettings settings)
    {
        var needsSave = false;

        if (settings.Llms == null || settings.Llms.Count == 0)
        {
            var serverId = Guid.NewGuid();
            settings.Llms = new List<LlmServerConfig>
            {
                new()
                {
                    Id = serverId,
                    Name = "Ollama",
                    ServerType = ServerType.Ollama,
                    BaseUrl = settings.OllamaServerUrl,
                    Password = settings.OllamaBasicAuthPassword,
                    IgnoreSslErrors = settings.IgnoreSslErrors,
                    HttpTimeoutSeconds = settings.HttpTimeoutSeconds,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            };
            settings.DefaultLlmId = serverId;
            needsSave = true;
        }
        else if (settings.DefaultLlmId == null && settings.Llms.Count > 0)
        {
            if (settings.Llms[0].Id == null)
                settings.Llms[0].Id = Guid.NewGuid();

            settings.DefaultLlmId = settings.Llms[0].Id;
            needsSave = true;
        }

        if (needsSave)
            await SaveSettingsAsync(settings);

        return settings;
    }
}
