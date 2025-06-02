using ChatClient.Shared.Models;
using ChatClient.Api.Services;

namespace ChatClient.Api.Client.Services;

public class ModelsService(OllamaService ollamaService) : IModelsService
{
    public async Task<List<OllamaModel>> GetModelsAsync()
    {
        return await ollamaService.GetModelsAsync();
    }
}
