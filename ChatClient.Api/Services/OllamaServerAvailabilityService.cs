using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class OllamaServerAvailabilityService(
    IOllamaClientService ollamaService,
    IUserSettingsService userSettingsService,
    ILogger<OllamaServerAvailabilityService> logger)
{
    public async Task<OllamaServerStatus> CheckOllamaStatusAsync(Guid? serverId = null)
    {
        try
        {
            var settings = await userSettingsService.GetSettingsAsync();
            var serverToCheck = serverId ?? settings.DefaultLlmId;

            if (!serverToCheck.HasValue)
            {
                logger.LogWarning("No default server configured");
                return new OllamaServerStatus
                {
                    IsAvailable = false,
                    ErrorMessage = "No default server configured"
                };
            }

            // Find server in settings
            var server = settings.Llms.FirstOrDefault(s => s.Id == serverToCheck.Value);
            if (server == null)
            {
                logger.LogWarning("Server not found: {ServerId}", serverToCheck.Value);
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

            var models = await ollamaService.GetModelsAsync(serverToCheck.Value);
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
