using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class McpServerConfigService(IMcpServerConfigRepository repository) : IMcpServerConfigService
{
    private readonly IMcpServerConfigRepository _repository = repository;

    public Task<IReadOnlyCollection<McpServerConfig>> GetAllAsync() => _repository.GetAllAsync();

    public async Task<McpServerConfig?> GetByIdAsync(Guid serverId)
    {
        var servers = await _repository.GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == serverId);
    }

    public async Task CreateAsync(McpServerConfig serverConfig)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        serverConfig.Id ??= Guid.NewGuid();
        serverConfig.CreatedAt = DateTime.UtcNow;
        serverConfig.UpdatedAt = DateTime.UtcNow;
        servers.Add(serverConfig);
        await _repository.SaveAllAsync(servers);
    }

    public async Task UpdateAsync(McpServerConfig serverConfig)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        var index = servers.FindIndex(s => s.Id == serverConfig.Id);
        if (index == -1)
            throw new KeyNotFoundException($"MCP server config with ID {serverConfig.Id} not found");
        serverConfig.UpdatedAt = DateTime.UtcNow;
        servers[index] = serverConfig;
        await _repository.SaveAllAsync(servers);
    }

    public async Task DeleteAsync(Guid serverId)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        var existing = servers.FirstOrDefault(s => s.Id == serverId) ??
                       throw new KeyNotFoundException($"MCP server config with ID {serverId} not found");
        servers.Remove(existing);
        await _repository.SaveAllAsync(servers);
    }
}

