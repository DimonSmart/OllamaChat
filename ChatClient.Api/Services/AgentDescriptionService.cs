using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class AgentDescriptionService : IAgentDescriptionService
{
    private readonly string _filePath;
    private readonly ILogger<AgentDescriptionService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AgentDescriptionService(IConfiguration configuration, ILogger<AgentDescriptionService> logger)
    {
        var promptsFilePath = configuration["AgentDescriptions:FilePath"] ?? "agent_descriptions.json";
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
        var defaultAgents = new List<AgentDescription>
        {
            new AgentDescription
            {
                AgentName = "Default Assistant",
                Content = "You are a helpful assistant."
            },
            new AgentDescription
            {
                AgentName = "Code Assistant",
                Content = "You are a coding assistant. Help the user write and understand code."
            }
        };

        WriteToFile(defaultAgents);
    }


    public async Task<List<AgentDescription>> GetAllAsync()
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

    public async Task<AgentDescription?> GetByIdAsync(Guid id)
    {
        var agents = await GetAllAsync();
        return agents.FirstOrDefault(p => p.Id == id);
    }

    public async Task<AgentDescription> CreateAsync(AgentDescription prompt)
    {
        try
        {
            await _semaphore.WaitAsync();

            var agents = await ReadFromFileAsync();

            if (prompt.Id == Guid.Empty)
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

    public async Task<AgentDescription> UpdateAsync(AgentDescription prompt)
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

    public async Task DeleteAsync(Guid id)
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

    private async Task<List<AgentDescription>> ReadFromFileAsync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_filePath);
        var agents = JsonSerializer.Deserialize<List<AgentDescription>>(json) ?? [];

        var updated = false;
        foreach (var agent in agents.Where(a => a.Id == Guid.Empty))
        {
            agent.Id = Guid.NewGuid();
            updated = true;
        }

        if (updated)
            await WriteToFileAsync(agents);

        return agents;
    }

    private static string SerializeAgents(List<AgentDescription> agents)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(agents, options);
    }

    private async Task WriteToFileAsync(List<AgentDescription> agents)
    {
        await File.WriteAllTextAsync(_filePath, SerializeAgents(agents));
    }

    private void WriteToFile(List<AgentDescription> agents)
    {
        File.WriteAllText(_filePath, SerializeAgents(agents));
    }

    public AgentDescription GetDefaultAgentDescription() => new()
    {
        Id = Guid.NewGuid(),
        AgentName = "Default Assistant",
        Content = "You are a helpful AI assistant. Please format your responses using Markdown."
    };
}
