using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IModelsService
{
    Task<List<OllamaModel>> GetModelsAsync();
}
