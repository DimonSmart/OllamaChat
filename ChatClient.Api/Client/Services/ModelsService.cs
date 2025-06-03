using ChatClient.Api.Services;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public class ModelsService(OllamaService ollamaService) : IModelsService
{
    public async Task<List<OllamaModel>> GetModelsAsync()
    {
        return await ollamaService.GetModelsAsync();
    }
}