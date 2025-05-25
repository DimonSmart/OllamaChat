using ChatClient.Shared.Models;
using System.Net.Http.Json;

namespace ChatClient.Api.Client.Services;

public class ModelsService(HttpClient httpClient) : IModelsService
{
    public async Task<List<OllamaModel>> GetModelsAsync()
    {
        try
        {
            var models = await httpClient.GetFromJsonAsync<List<OllamaModel>>("api/models");
            return models ?? [];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching models: {ex.Message}");
            return [];
        }
    }
}
