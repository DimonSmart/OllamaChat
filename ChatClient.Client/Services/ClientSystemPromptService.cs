using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatClient.Client.Services;

public class ClientSystemPromptService : ISystemPromptService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new() 
    { 
        PropertyNameCaseInsensitive = true 
    };

    public ClientSystemPromptService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<SystemPrompt>> GetAllPromptsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("api/systemprompts");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<SystemPrompt>>(_jsonOptions) ?? 
                       new List<SystemPrompt>();
            }

            Console.WriteLine($"Error loading system prompts: {response.StatusCode}");
            return new List<SystemPrompt>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception loading system prompts: {ex}");
            return new List<SystemPrompt>();
        }
    }
    
    public async Task<SystemPrompt?> GetPromptByIdAsync(string id)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/systemprompts/{id}");
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<SystemPrompt>(_jsonOptions);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting system prompt: {ex}");
            return null;
        }
    }

    public async Task<SystemPrompt> CreatePromptAsync(SystemPrompt prompt)
    {
        var response = await _httpClient.PostAsJsonAsync("api/systemprompts", prompt);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SystemPrompt>(_jsonOptions) ?? prompt;
    }

    public async Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/systemprompts/{prompt.Id}", prompt);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SystemPrompt>(_jsonOptions) ?? prompt;
    }

    public async Task DeletePromptAsync(string id)
    {
        var response = await _httpClient.DeleteAsync($"api/systemprompts/{id}");
        response.EnsureSuccessStatusCode();
    }

    public SystemPrompt GetDefaultSystemPrompt() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}
