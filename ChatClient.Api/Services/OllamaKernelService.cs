using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public class OllamaKernelService(
    IOllamaClientService ollamaClientService,
    ILogger<OllamaKernelService> logger) : IOllamaKernelService
{
    public async Task<IChatCompletionService> GetClientAsync(Guid serverId)
    {
        logger.LogInformation("Creating Ollama chat completion service for server {ServerId}", serverId);
        var ollamaClient = await ollamaClientService.GetClientAsync(serverId);
        return ollamaClient.AsChatCompletionService();
    }
}