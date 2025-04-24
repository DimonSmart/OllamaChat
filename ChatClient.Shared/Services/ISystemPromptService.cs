using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface ISystemPromptService
{
    Task<List<SystemPrompt>> GetAllPromptsAsync();
    Task<SystemPrompt?> GetPromptByIdAsync(string id);
    Task<SystemPrompt> CreatePromptAsync(SystemPrompt prompt);
    Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt);
    Task DeletePromptAsync(string id);
}
