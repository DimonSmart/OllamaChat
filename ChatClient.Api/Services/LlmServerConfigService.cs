using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class LlmServerConfigService : ILlmServerConfigService
{
    private readonly string _filePath;
    private readonly ILogger<LlmServerConfigService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public LlmServerConfigService(IConfiguration configuration, ILogger<LlmServerConfigService> logger)
    {
        var serversFilePath = configuration["LlmServers:FilePath"] ?? FilePathConstants.DefaultLlmServersFile;
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
        return await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            if (!File.Exists(_filePath))
            {
                await CreateDefaultServersFile();
                return await ReadFromFileAsync();
            }

            return await ReadFromFileAsync();
        }, _logger, "Error getting LLM server configs");
    }

    public async Task<LlmServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task CreateAsync(LlmServerConfig serverConfig)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();

            serverConfig.Id ??= Guid.NewGuid();
            serverConfig.CreatedAt = DateTime.UtcNow;
            serverConfig.UpdatedAt = DateTime.UtcNow;

            servers.Add(serverConfig);
            await WriteToFileAsync(servers);
        }, _logger, "Error creating LLM server config");
    }

    public async Task UpdateAsync(LlmServerConfig serverConfig)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();
            var existingIndex = servers.FindIndex(s => s.Id == serverConfig.Id);

            if (existingIndex == -1)
                throw new KeyNotFoundException($"LLM server config with ID {serverConfig.Id} not found");

            serverConfig.UpdatedAt = DateTime.UtcNow;
            servers[existingIndex] = serverConfig;

            await WriteToFileAsync(servers);
        }, _logger, "Error updating LLM server config");
    }

    public async Task DeleteAsync(Guid id)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();
            var existingServer = servers.FirstOrDefault(s => s.Id == id);

            if (existingServer == null)
                throw new KeyNotFoundException($"LLM server config with ID {id} not found");

            servers.Remove(existingServer);
            await WriteToFileAsync(servers);
        }, _logger, "Error deleting LLM server config");
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
