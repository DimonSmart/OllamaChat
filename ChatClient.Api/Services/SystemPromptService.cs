using System.Text.Json;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class SystemPromptService : ISystemPromptService
{
    private readonly string _filePath;
    private readonly ILogger<SystemPromptService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SystemPromptService(IConfiguration configuration, ILogger<SystemPromptService> logger)
    {
        var promptsFilePath = configuration["SystemPrompts:FilePath"] ?? "system_prompts.json";
        _filePath = Path.GetFullPath(promptsFilePath);
        _logger = logger;
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_filePath))
        {
            CreateDefaultPromptsFile().GetAwaiter().GetResult();
        }
        else
        {
            MigratePromptFile().GetAwaiter().GetResult();
        }
    }

    private async Task CreateDefaultPromptsFile()
    {
        var defaultPrompts = new List<SystemPrompt>
        {
            new SystemPrompt
            {
                Name = "Default Assistant",
                Content = "You are a helpful assistant."
            },
            new SystemPrompt
            {
                Name = "Code Assistant",
                Content = "You are a coding assistant. Help the user write and understand code."
            }
        };

        await WriteToFileAsync(defaultPrompts);
    }

    private async Task MigratePromptFile()
    {
        var json = await File.ReadAllTextAsync(_filePath);
        if (!json.Contains("ModelName"))
        {
            var prompts = JsonSerializer.Deserialize<List<SystemPrompt>>(json) ?? [];
            await WriteToFileAsync(prompts);
        }
    }

    public async Task<List<SystemPrompt>> GetAllPromptsAsync()
    {
        try
        {
            await _semaphore.WaitAsync();

            if (!File.Exists(_filePath))
            {
                await CreateDefaultPromptsFile();
                return await ReadFromFileAsync();
            }

            return await ReadFromFileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system prompts");
            return [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<SystemPrompt?> GetPromptByIdAsync(Guid id)
    {
        var prompts = await GetAllPromptsAsync();
        return prompts.FirstOrDefault(p => p.Id == id);
    }

    public async Task<SystemPrompt> CreatePromptAsync(SystemPrompt prompt)
    {
        try
        {
            await _semaphore.WaitAsync();

            var prompts = await ReadFromFileAsync();

            if (prompt.Id == null)
                prompt.Id = Guid.NewGuid();

            prompt.CreatedAt = DateTime.UtcNow;
            prompt.UpdatedAt = DateTime.UtcNow;

            prompts.Add(prompt);
            await WriteToFileAsync(prompts);

            return prompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating system prompt");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<SystemPrompt> UpdatePromptAsync(SystemPrompt prompt)
    {
        try
        {
            await _semaphore.WaitAsync();

            var prompts = await ReadFromFileAsync();
            var existingIndex = prompts.FindIndex(p => p.Id == prompt.Id);

            if (existingIndex == -1)
            {
                throw new KeyNotFoundException($"System prompt with ID {prompt.Id} not found");
            }

            prompt.UpdatedAt = DateTime.UtcNow;
            prompts[existingIndex] = prompt;

            await WriteToFileAsync(prompts);

            return prompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating system prompt");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeletePromptAsync(Guid id)
    {
        try
        {
            await _semaphore.WaitAsync();

            var prompts = await ReadFromFileAsync();
            var existingPrompt = prompts.FirstOrDefault(p => p.Id == id);

            if (existingPrompt == null)
            {
                throw new KeyNotFoundException($"System prompt with ID {id} not found");
            }

            prompts.Remove(existingPrompt);
            await WriteToFileAsync(prompts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting system prompt");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<List<SystemPrompt>> ReadFromFileAsync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<List<SystemPrompt>>(json) ?? [];
    }

    private async Task WriteToFileAsync(List<SystemPrompt> prompts)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(prompts, options);
        await File.WriteAllTextAsync(_filePath, json);
    }

    public SystemPrompt GetDefaultSystemPrompt() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}
