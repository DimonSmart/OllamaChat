using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class McpServerConfigService : IMcpServerConfigService
{
    private readonly JsonFileRepository<List<McpServerConfig>> _repository;

    public McpServerConfigService(IConfiguration configuration, ILogger<McpServerConfigService> logger)
    {
        var serversFilePath = configuration["McpServers:FilePath"] ?? FilePathConstants.DefaultMcpServersFile;
        _repository = new JsonFileRepository<List<McpServerConfig>>(serversFilePath, logger);
    }

    public async Task<List<McpServerConfig>> GetAllAsync() => await _repository.ReadAsync() ?? [];

    public async Task<McpServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task CreateAsync(McpServerConfig serverConfig)
    {
        await _repository.UpdateAsync(servers =>
        {
            serverConfig.Id ??= Guid.NewGuid();
            serverConfig.CreatedAt = DateTime.UtcNow;
            serverConfig.UpdatedAt = DateTime.UtcNow;

            servers.Add(serverConfig);
            return Task.CompletedTask;
        }, []);
    }

    public async Task UpdateAsync(McpServerConfig serverConfig)
    {
        await _repository.UpdateAsync(servers =>
        {
            var index = servers.FindIndex(s => s.Id == serverConfig.Id);

            if (index == -1)
                throw new KeyNotFoundException($"MCP server config with ID {serverConfig.Id} not found");

            serverConfig.UpdatedAt = DateTime.UtcNow;
            servers[index] = serverConfig;

            return Task.CompletedTask;
        }, []);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _repository.UpdateAsync(servers =>
        {
            var existing = servers.FirstOrDefault(s => s.Id == id) ??
                           throw new KeyNotFoundException($"MCP server config with ID {id} not found");

            servers.Remove(existing);
            return Task.CompletedTask;
        }, []);
    }
}
