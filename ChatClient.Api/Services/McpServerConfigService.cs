using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Text.Json;

namespace ChatClient.Api.Services;

public class McpServerConfigService : IMcpServerConfigService
{
    private readonly string _filePath;
    private readonly ILogger<McpServerConfigService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public McpServerConfigService(IConfiguration configuration, ILogger<McpServerConfigService> logger)
    {
        var serversFilePath = configuration["McpServers:FilePath"] ?? FilePathConstants.DefaultMcpServersFile;
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
        var defaultServers = new List<McpServerConfig>
        {
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "Time Server",
                Command = "DimonSmart.NugetMcpServer",
                SamplingModel = null, // Will use user's default model
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new McpServerConfig
            {
                Id = Guid.NewGuid(),
                Name = "LLM.txt MCP Server",
                Sse = "https://mcp.llmtxt.dev/sse",
                SamplingModel = null, // Will use user's default model
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await WriteToFileAsync(defaultServers);
    }

    public async Task<List<McpServerConfig>> GetAllAsync()
    {
        return await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            if (!File.Exists(_filePath))
            {
                await CreateDefaultServersFile();
                return await ReadFromFileAsync();
            }

            return await ReadFromFileAsync();
        }, _logger, "Error getting MCP server configs");
    }

    public async Task<McpServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task CreateAsync(McpServerConfig serverConfig)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();

            serverConfig.Id ??= Guid.NewGuid();
            serverConfig.CreatedAt = DateTime.UtcNow;
            serverConfig.UpdatedAt = DateTime.UtcNow;

            servers.Add(serverConfig);
            await WriteToFileAsync(servers);
        }, _logger, "Error creating MCP server config");
    }

    public async Task UpdateAsync(McpServerConfig serverConfig)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();
            var existingIndex = servers.FindIndex(s => s.Id == serverConfig.Id);

            if (existingIndex == -1)
                throw new KeyNotFoundException($"MCP server config with ID {serverConfig.Id} not found");

            serverConfig.UpdatedAt = DateTime.UtcNow;
            servers[existingIndex] = serverConfig;

            await WriteToFileAsync(servers);
        }, _logger, "Error updating MCP server config");
    }

    public async Task DeleteAsync(Guid id)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(_semaphore, async () =>
        {
            var servers = await ReadFromFileAsync();
            var existingServer = servers.FirstOrDefault(s => s.Id == id);

            if (existingServer == null)
                throw new KeyNotFoundException($"MCP server config with ID {id} not found");

            servers.Remove(existingServer);
            await WriteToFileAsync(servers);
        }, _logger, "Error deleting MCP server config");
    }

    private async Task<List<McpServerConfig>> ReadFromFileAsync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<List<McpServerConfig>>(json) ?? [];
    }

    private async Task WriteToFileAsync(List<McpServerConfig> servers)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(servers, options);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
