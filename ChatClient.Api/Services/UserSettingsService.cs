using ChatClient.Shared.Constants;
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
    private readonly ILlmServerConfigService _llmServerConfigService;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public event Func<Task>? EmbeddingModelChanged;

    public UserSettingsService(IConfiguration configuration, ILogger<UserSettingsService> logger, ILlmServerConfigService llmServerConfigService)
    {
        _configuration = configuration;
        _logger = logger;
        _llmServerConfigService = llmServerConfigService;

        var userSettingsFilePath = configuration["UserSettings:FilePath"] ?? FilePathConstants.DefaultUserSettingsFile;
        _settingsFilePath = Path.GetFullPath(userSettingsFilePath);

        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _logger.LogInformation("User settings file path: {FilePath}", _settingsFilePath);
    }

    public async Task<UserSettings> GetSettingsAsync()
    {
        UserSettings settings;

        if (!File.Exists(_settingsFilePath))
        {
            _logger.LogInformation("Settings file not found. Creating a new one with default settings");
            settings = new UserSettings();
        }
        else
        {
            var json = await File.ReadAllTextAsync(_settingsFilePath);
            settings = JsonSerializer.Deserialize<UserSettings>(json, _jsonOptions) ?? new UserSettings();
        }

        var updated = false;

        if (settings.DefaultLlmId == null)
        {
            var servers = await _llmServerConfigService.GetAllAsync();
            var defaultServer = servers.FirstOrDefault(s => s.ServerType == ServerType.Ollama);
            if (defaultServer != null)
            {
                settings.DefaultLlmId = defaultServer.Id;
                updated = true;
            }
        }

        if (updated || !File.Exists(_settingsFilePath))
            await SaveSettingsAsync(settings);

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
