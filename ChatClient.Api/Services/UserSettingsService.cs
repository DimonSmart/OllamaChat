using ChatClient.Api.Repositories;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class UserSettingsService(JsonFileRepository<UserSettings> repository, ILogger<UserSettingsService> logger, ILlmServerConfigService llmServerConfigService) : IUserSettingsService
{
    private readonly JsonFileRepository<UserSettings> _repository = repository;
    private readonly ILogger<UserSettingsService> _logger = logger;
    private readonly ILlmServerConfigService _llmServerConfigService = llmServerConfigService;

    public async Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _repository.ReadAsync(cancellationToken) ?? new UserSettings();
        var updated = false;

        if (settings.DefaultModel.ServerId is null)
        {
            var servers = await _llmServerConfigService.GetAllAsync();
            var defaultServer = servers.FirstOrDefault(s => s.ServerType == ServerType.Ollama);
            if (defaultServer != null)
            {
                settings.DefaultModel = settings.DefaultModel with { ServerId = defaultServer.Id };
                updated = true;
            }
        }

        settings.Embedding ??= new EmbeddingSettings();

        if (updated || !_repository.Exists)
            await SaveSettingsAsync(settings, cancellationToken);

        return settings;
    }

    public async Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            await _repository.WriteAsync(settings, cancellationToken);
            _logger.LogInformation("User settings saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving user settings");
        }
    }
}
