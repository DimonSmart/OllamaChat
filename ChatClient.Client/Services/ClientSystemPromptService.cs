using System.Net.Http.Json;
using System.Text.Json;
using ChatClient.Shared.Models;

namespace ChatClient.Client.Services;

public class ClientSystemPromptService
{
    private readonly HttpClient _httpClient;
    
    public ClientSystemPromptService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    public async Task<List<SystemPrompt>> GetSystemPromptsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/systemprompts");
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<SystemPrompt>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<SystemPrompt>();
            }
            
            return new List<SystemPrompt>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading system prompts: {ex}");
            return new List<SystemPrompt>();
        }
    }
    
    public SystemPrompt GetDefaultSystemPrompt()
    {
        return new SystemPrompt
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Default Assistant",
            Content = "You are a helpful AI assistant. Please format your responses using Markdown."
        };
    }
}
