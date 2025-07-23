using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class StartupOllamaChecker(IOllamaService ollamaService, ILogger<StartupOllamaChecker> logger)
{
    public async Task<OllamaServerStatus> CheckOllamaStatusAsync()
    {
        try
        {
            var models = await ollamaService.GetModelsAsync();
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
