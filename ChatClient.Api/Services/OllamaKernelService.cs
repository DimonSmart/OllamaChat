using ChatClient.Application.Services;
using ChatClient.Domain.Models;
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
