using System.Text.Json;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class McpServerConfigService : IMcpServerConfigService
{
    private readonly string _filePath;
    private readonly ILogger<McpServerConfigService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public McpServerConfigService(IConfiguration configuration, ILogger<McpServerConfigService> logger)
    {
        var serversFilePath = configuration["McpServers:FilePath"] ?? "Data/mcp_servers.json";
        _filePath = Path.GetFullPath(serversFilePath);
        _logger = logger;

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create default file if it doesn't exist
        if (!File.Exists(_filePath))
        {
            CreateDefaultServersFile().GetAwaiter().GetResult();
        }
    }    private async Task CreateDefaultServersFile()
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

    public async Task<List<McpServerConfig>> GetAllServersAsync()
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
            _logger.LogError(ex, "Error getting MCP server configs");
            return [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<McpServerConfig?> GetServerByIdAsync(Guid id)
    {
        var servers = await GetAllServersAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task<McpServerConfig> CreateServerAsync(McpServerConfig server)
    {
        try
        {
            await _semaphore.WaitAsync();

            var servers = await ReadFromFileAsync();

            if (server.Id == null) server.Id = Guid.NewGuid();

            server.CreatedAt = DateTime.UtcNow;
            server.UpdatedAt = DateTime.UtcNow;

            servers.Add(server);
            await WriteToFileAsync(servers);

            return server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating MCP server config");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<McpServerConfig> UpdateServerAsync(McpServerConfig server)
    {
        try
        {
            await _semaphore.WaitAsync();

            var servers = await ReadFromFileAsync();
            var existingIndex = servers.FindIndex(s => s.Id == server.Id);

            if (existingIndex == -1)
            {
                throw new KeyNotFoundException($"MCP server config with ID {server.Id} not found");
            }

            server.UpdatedAt = DateTime.UtcNow;
            servers[existingIndex] = server;

            await WriteToFileAsync(servers);

            return server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating MCP server config");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteServerAsync(Guid id)
    {
        try
        {
            await _semaphore.WaitAsync();

            var servers = await ReadFromFileAsync();
            var existingServer = servers.FirstOrDefault(s => s.Id == id);

            if (existingServer == null)
            {
                throw new KeyNotFoundException($"MCP server config with ID {id} not found");
            }

            servers.Remove(existingServer);
            await WriteToFileAsync(servers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting MCP server config");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
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
