using System.Text.Json;

using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class LlmServerConfigService : ILlmServerConfigService
{
    private readonly string _filePath;
    private readonly ILogger<LlmServerConfigService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public LlmServerConfigService(IConfiguration configuration, ILogger<LlmServerConfigService> logger)
    {
        var serversFilePath = configuration["LlmServers:FilePath"] ?? "Data/llm_servers.json";
        _filePath = Path.GetFullPath(serversFilePath);
        _logger = logger;
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_filePath))
        {
            CreateDefaultServersFile().GetAwaiter().GetResult();
        }
    }

    private async Task CreateDefaultServersFile()
    {
        var defaultServers = new List<LlmServerConfig>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Ollama",
                ServerType = ServerType.Ollama,
                BaseUrl = "http://localhost:11434",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await WriteToFileAsync(defaultServers);
    }

    public async Task<List<LlmServerConfig>> GetAllAsync()
    {
        try
        {
            await _semaphore.WaitAsync();

            if (!File.Exists(_filePath))
            {
                await CreateDefaultServersFile();
                return await ReadFromFileAsync();
            }

            return await ReadFromFileAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting LLM server configs");
            return [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<LlmServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task<LlmServerConfig> CreateAsync(LlmServerConfig server)
    {
        try
        {
            await _semaphore.WaitAsync();

            var servers = await ReadFromFileAsync();

            if (server.Id == null)
                server.Id = Guid.NewGuid();

            server.CreatedAt = DateTime.UtcNow;
            server.UpdatedAt = DateTime.UtcNow;

            servers.Add(server);
            await WriteToFileAsync(servers);

            return server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating LLM server config");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<LlmServerConfig> UpdateAsync(LlmServerConfig server)
    {
        try
        {
            await _semaphore.WaitAsync();

            var servers = await ReadFromFileAsync();
            var existingIndex = servers.FindIndex(s => s.Id == server.Id);

            if (existingIndex == -1)
            {
                throw new KeyNotFoundException($"LLM server config with ID {server.Id} not found");
            }

            server.UpdatedAt = DateTime.UtcNow;
            servers[existingIndex] = server;

            await WriteToFileAsync(servers);

            return server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating LLM server config");
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

            var servers = await ReadFromFileAsync();
            var existingServer = servers.FirstOrDefault(s => s.Id == id);

            if (existingServer == null)
            {
                throw new KeyNotFoundException($"LLM server config with ID {id} not found");
            }

            servers.Remove(existingServer);
            await WriteToFileAsync(servers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting LLM server config");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<List<LlmServerConfig>> ReadFromFileAsync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<List<LlmServerConfig>>(json) ?? [];
    }

    private async Task WriteToFileAsync(List<LlmServerConfig> servers)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(servers, options);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
