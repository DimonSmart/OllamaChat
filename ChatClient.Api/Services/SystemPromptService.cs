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
            CreateDefaultAgentsFile();
        }
    }

    private void CreateDefaultAgentsFile()
    {
        var defaultAgents = new List<SystemPrompt>
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

        WriteToFile(defaultAgents);
    }


    public async Task<List<SystemPrompt>> GetAllPromptsAsync()
    {
        try
        {
            await _semaphore.WaitAsync();

            if (!File.Exists(_filePath))
            {
                CreateDefaultAgentsFile();
                return await ReadFromFileAsync();
            }

            return await ReadFromFileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents");
            return [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<SystemPrompt?> GetPromptByIdAsync(Guid id)
    {
        var agents = await GetAllPromptsAsync();
        return agents.FirstOrDefault(p => p.Id == id);
    }

    public async Task<SystemPrompt> CreatePromptAsync(SystemPrompt prompt)
    {
        try
        {
            await _semaphore.WaitAsync();

            var agents = await ReadFromFileAsync();

            if (prompt.Id == null)
                prompt.Id = Guid.NewGuid();

            prompt.CreatedAt = DateTime.UtcNow;
            prompt.UpdatedAt = DateTime.UtcNow;

            agents.Add(prompt);
            await WriteToFileAsync(agents);

            return prompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating agent");
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

            var agents = await ReadFromFileAsync();
            var existingIndex = agents.FindIndex(p => p.Id == prompt.Id);

            if (existingIndex == -1)
            {
                throw new KeyNotFoundException($"Agent with ID {prompt.Id} not found");
            }

            prompt.UpdatedAt = DateTime.UtcNow;
            agents[existingIndex] = prompt;

            await WriteToFileAsync(agents);

            return prompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent");
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

            var agents = await ReadFromFileAsync();
            var existingAgent = agents.FirstOrDefault(p => p.Id == id);

            if (existingAgent == null)
            {
                throw new KeyNotFoundException($"Agent with ID {id} not found");
            }

            agents.Remove(existingAgent);
            await WriteToFileAsync(agents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting agent");
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

    private static string SerializeAgents(List<SystemPrompt> agents)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(agents, options);
    }

    private async Task WriteToFileAsync(List<SystemPrompt> agents)
    {
        await File.WriteAllTextAsync(_filePath, SerializeAgents(agents));
    }

    private void WriteToFile(List<SystemPrompt> agents)
    {
        File.WriteAllText(_filePath, SerializeAgents(agents));
    }

    public SystemPrompt GetDefaultSystemPrompt() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}
