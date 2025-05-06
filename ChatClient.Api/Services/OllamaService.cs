using ChatClient.Shared.Models;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class OllamaService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OllamaService> logger)
{
    public async Task<List<OllamaModel>> GetModelsAsync()
    {
        try
        {
            var baseUrl = configuration["Ollama:BaseUrl"]!;
            var httpClient = httpClientFactory.CreateClient("OllamaClient");
            httpClient.BaseAddress = new Uri(baseUrl);

            var response = await httpClient.GetAsync("/api/tags");
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonSerializer.Deserialize<OllamaModelsResponse>(content);
            
            return ollamaResponse?.Models ?? new List<OllamaModel>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve Ollama models: {Message}", ex.Message);
            return [];
        }
    }
}
