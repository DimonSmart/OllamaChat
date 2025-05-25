using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface ISystemPromptService
{
    Task<List<SystemPrompt>> GetAllPromptsAsync();
    Task<SystemPrompt?> GetPromptByIdAsync(Guid id);
    Task<SystemPrompt> CreatePromptAsync(SystemPrompt prompt);
    Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt);
    Task DeletePromptAsync(Guid id);
}
