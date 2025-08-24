using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using ChatClient.Shared.Constants;

namespace ChatClient.Api.Services;

public class UserSettingsService : IUserSettingsService
{
    private const int CurrentVersion = 2;
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
        using var doc = JsonDocument.Parse(json);
        var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();
        var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
        settings.Version = version;

        if (version < CurrentVersion)
        {
            var migrated = await MigrateSettingsAsync(settings, doc.RootElement);
            await SaveSettingsAsync(migrated);
            return migrated;
        }

        return settings;
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

            settings.Version = CurrentVersion;
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

    private async Task<UserSettings> MigrateSettingsAsync(UserSettings settings, JsonElement root)
    {
        var serverId = Guid.NewGuid();
        var baseUrl = root.TryGetProperty("ollamaServerUrl", out var urlEl) ? urlEl.GetString() : null;
        var password = root.TryGetProperty("ollamaBasicAuthPassword", out var passEl) ? passEl.GetString() : null;
        var ignoreSsl = root.TryGetProperty("ignoreSslErrors", out var sslEl) && sslEl.GetBoolean();
        var httpTimeout = root.TryGetProperty("httpTimeoutSeconds", out var tEl) ? tEl.GetInt32() : 600;

        if (settings.Llms == null || settings.Llms.Count == 0)
        {
            settings.Llms = new List<LlmServerConfig>
            {
                new()
                {
                    Id = serverId,
                    Name = "Ollama",
                    ServerType = ServerType.Ollama,
                    BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? OllamaDefaults.ServerUrl : baseUrl,
                    Password = password,
                    IgnoreSslErrors = ignoreSsl,
                    HttpTimeoutSeconds = httpTimeout,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            };
            settings.DefaultLlmId = serverId;
        }
        else if (settings.DefaultLlmId == null && settings.Llms.Count > 0)
        {
            if (settings.Llms[0].Id == null)
                settings.Llms[0].Id = Guid.NewGuid();

            settings.DefaultLlmId = settings.Llms[0].Id;
        }

        settings.Version = CurrentVersion;
        return settings;
    }
}
