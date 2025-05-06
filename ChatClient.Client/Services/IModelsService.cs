using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public interface IModelsService
{
    Task<List<OllamaModel>> GetModelsAsync();
}
