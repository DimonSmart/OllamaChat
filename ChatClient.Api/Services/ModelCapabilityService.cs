using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class ModelCapabilityService(
    ILlmServerConfigService llmServerConfigService,
    IOllamaClientService ollamaService,
    ILogger<ModelCapabilityService> logger) : IModelCapabilityService
{
    public async Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default)
    {
        var server = await llmServerConfigService.GetByIdAsync(model.ServerId);
        if (server is null)
        {
            logger.LogWarning("Server {ServerId} not found while checking function support for model {ModelName}", model.ServerId, model.ModelName);
            return true;
        }

        if (server.ServerType != ServerType.Ollama)
        {
            return true;
        }

        try
        {
            var models = await ollamaService.GetModelsAsync(model.ServerId);
            var matchingModel = models.FirstOrDefault(m =>
                string.Equals(m.Name, model.ModelName, StringComparison.OrdinalIgnoreCase));

            if (matchingModel is null)
            {
                logger.LogWarning("Model {ModelName} was not found on Ollama server {ServerName}. Assuming function support is unknown.", model.ModelName, server.Name);
                return true;
            }

            return matchingModel.SupportsFunctionCalling;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to determine function support for model {ModelName} on server {ServerName}. Assuming support.", model.ModelName, server.Name);
            return true;
        }
    }
}
