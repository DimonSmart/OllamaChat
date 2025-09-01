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

        if (settings.DefaultModel.ServerId == Guid.Empty)
        {
            var servers = await _llmServerConfigService.GetAllAsync();
            var defaultServer = servers.FirstOrDefault(s => s.ServerType == ServerType.Ollama);
            if (defaultServer != null)
            {
                settings.DefaultModel = settings.DefaultModel with { ServerId = defaultServer.Id ?? Guid.Empty };
                updated = true;
            }
        }

        settings.Embedding ??= new EmbeddingSettings();

        if (updated || !File.Exists(_settingsFilePath))
            await SaveSettingsAsync(settings);

        return settings;
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
