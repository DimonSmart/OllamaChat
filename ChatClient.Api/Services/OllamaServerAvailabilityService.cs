using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class OllamaServerAvailabilityService(
    IOllamaClientService ollamaService,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    ILogger<OllamaServerAvailabilityService> logger)
{
    public async Task<OllamaServerStatus> CheckOllamaStatusAsync(Guid? serverId = null)
    {
        try
        {
            var settings = await userSettingsService.GetSettingsAsync();
            var serverToCheck = serverId ?? settings.DefaultModel.ServerId;

            if (serverToCheck == Guid.Empty)
            {
                logger.LogWarning("No default server configured");
                return new OllamaServerStatus
                {
                    IsAvailable = false,
                    ErrorMessage = "No default server configured"
                };
            }

            // Find server in settings
            var servers = await llmServerConfigService.GetAllAsync();
            var server = servers.FirstOrDefault(s => s.Id == serverToCheck);
            if (server == null)
            {
                logger.LogWarning("Server not found: {ServerId}", serverToCheck);
                return new OllamaServerStatus
                {
                    IsAvailable = false,
                    ErrorMessage = "Server configuration not found"
                };
            }

            // Only check Ollama servers
            if (server.ServerType != ServerType.Ollama)
            {
                logger.LogInformation("Skipping non-Ollama server: {ServerName} (Type: {ServerType})",
                    server.Name, server.ServerType);
                return new OllamaServerStatus
                {
                    IsAvailable = true,
                    ErrorMessage = null
                };
            }

            var models = await ollamaService.GetModelsAsync(serverToCheck);
            logger.LogInformation("Ollama server '{ServerName}' is available with {ModelCount} models",
                server.Name, models.Count);

            return new OllamaServerStatus
            {
                IsAvailable = true,
                ErrorMessage = null
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama server check failed: {Message}", ex.Message);
            return OllamaStatusHelper.CreateStatusFromException(ex);
        }
    }
}
