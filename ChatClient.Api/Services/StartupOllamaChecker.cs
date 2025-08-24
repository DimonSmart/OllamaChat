using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class StartupOllamaChecker(IOllamaClientService ollamaService, ILogger<StartupOllamaChecker> logger)
{
    public async Task<OllamaServerStatus> CheckOllamaStatusAsync(Guid? serverId = null)
    {
        try
        {
            var models = await ollamaService.GetModelsAsync(serverId);
            logger.LogInformation("Ollama is available with {ModelCount} models", models.Count);

            return new OllamaServerStatus
            {
                IsAvailable = true,
                ErrorMessage = null
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama is not available: {Message}", ex.Message);
            return OllamaStatusHelper.CreateStatusFromException(ex);
        }
    }
}
