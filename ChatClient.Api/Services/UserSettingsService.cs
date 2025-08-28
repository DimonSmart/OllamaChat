using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class UserSettingsService : IUserSettingsService
{
    private readonly string _settingsFilePath;
    private readonly ILogger<UserSettingsService> _logger;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Func<Task>? EmbeddingModelChanged;

    public UserSettingsService(IConfiguration configuration, ILogger<UserSettingsService> logger)
    {
        _configuration = configuration;
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
            var defaultSettings = CreateDefaultSettings();
            await SaveSettingsAsync(defaultSettings);
            return defaultSettings;
        }

        var json = await File.ReadAllTextAsync(_settingsFilePath);
        var settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? CreateDefaultSettings();

        var defaults = GetDefaultRagValues();
        var updated = false;
        if (settings.RagLineChunkSize <= 0)
        {
            settings.RagLineChunkSize = defaults.line;
            updated = true;
        }
        if (settings.RagParagraphChunkSize <= 0)
        {
            settings.RagParagraphChunkSize = defaults.paragraph;
            updated = true;
        }
        if (settings.RagParagraphOverlap <= 0)
        {
            settings.RagParagraphOverlap = defaults.overlap;
            updated = true;
        }

        if (updated)
            await SaveSettingsAsync(settings);

        return settings;
    }

    private UserSettings CreateDefaultSettings()
    {
        var defaults = GetDefaultRagValues();
        return new UserSettings
        {
            RagLineChunkSize = defaults.line,
            RagParagraphChunkSize = defaults.paragraph,
            RagParagraphOverlap = defaults.overlap
        };
    }

    private (int line, int paragraph, int overlap) GetDefaultRagValues()
    {
        var line = _configuration.GetValue<int?>("RagIndex:MaxTokensPerLine") ?? 256;
        var paragraph = _configuration.GetValue<int?>("RagIndex:MaxTokensPerParagraph") ?? 512;
        var overlap = _configuration.GetValue<int?>("RagIndex:ParagraphOverlap") ?? 64;
        return (line, paragraph, overlap);
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

            if (existing != null &&
                (!string.Equals(existing.EmbeddingModelName, settings.EmbeddingModelName, StringComparison.OrdinalIgnoreCase) ||
                 existing.EmbeddingLlmId != settings.EmbeddingLlmId))
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
