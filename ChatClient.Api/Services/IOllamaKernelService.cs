using ChatClient.Domain.Models;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public interface IOllamaKernelService
{
    Task<IChatCompletionService> GetClientAsync(Guid serverId);
}
