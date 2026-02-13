using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class McpServerConfigService(IMcpServerConfigRepository repository) : IMcpServerConfigService
{
    private readonly IMcpServerConfigRepository _repository = repository;

    public async Task<IReadOnlyCollection<IMcpServerDescriptor>> GetAllAsync()
    {
        var externalServers = await LoadExternalServersAsync();

        var allServers = new List<IMcpServerDescriptor>(
            BuiltInMcpServerCatalog.Definitions.Count + externalServers.Count);
        allServers.AddRange(BuiltInMcpServerCatalog.Definitions);
        allServers.AddRange(externalServers);
        return allServers;
    }

    public async Task<IMcpServerDescriptor?> GetByIdAsync(Guid serverId)
    {
        if (BuiltInMcpServerCatalog.TryGetDefinition(serverId, out var definition) && definition is not null)
            return definition;

        var externalServers = await LoadExternalServersAsync();
        return externalServers.FirstOrDefault(s => s.Id == serverId);
    }

    public async Task CreateAsync(McpServerConfig serverConfig)
    {
        if (IsBuiltInServer(serverConfig))
            throw new InvalidOperationException("Built-in MCP servers are managed by the application and cannot be created manually.");

        var servers = await LoadExternalServersAsync();

        ValidateExternalServerConfig(serverConfig);

        serverConfig.Id ??= Guid.NewGuid();
        if (BuiltInMcpServerCatalog.IsBuiltInId(serverConfig.Id))
            throw new InvalidOperationException("Generated or provided ID conflicts with a built-in MCP server ID.");

        if (servers.Any(s => s.Id == serverConfig.Id))
            throw new InvalidOperationException($"MCP server config with ID {serverConfig.Id} already exists.");

        serverConfig.IsBuiltIn = false;
        serverConfig.BuiltInKey = null;
        serverConfig.CreatedAt = DateTime.UtcNow;
        serverConfig.UpdatedAt = DateTime.UtcNow;

        servers.Add(serverConfig);
        await _repository.SaveAllAsync(servers);
    }

    public async Task UpdateAsync(McpServerConfig serverConfig)
    {
        if (IsBuiltInServer(serverConfig))
            throw new InvalidOperationException("Built-in MCP servers cannot be edited.");

        var servers = await LoadExternalServersAsync();
        var index = servers.FindIndex(s => s.Id == serverConfig.Id);
        if (index == -1)
            throw new KeyNotFoundException($"MCP server config with ID {serverConfig.Id} not found");

        var existing = servers[index];
        if (existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in MCP servers cannot be edited.");
        }

        ValidateExternalServerConfig(serverConfig);

        serverConfig.IsBuiltIn = false;
        serverConfig.BuiltInKey = null;
        serverConfig.CreatedAt = existing.CreatedAt;
        serverConfig.UpdatedAt = DateTime.UtcNow;
        servers[index] = serverConfig;
        await _repository.SaveAllAsync(servers);
    }

    public async Task DeleteAsync(Guid serverId)
    {
        if (BuiltInMcpServerCatalog.IsBuiltInId(serverId))
            throw new InvalidOperationException("Built-in MCP servers cannot be deleted.");

        var servers = await LoadExternalServersAsync();
        var existing = servers.FirstOrDefault(s => s.Id == serverId) ??
                       throw new KeyNotFoundException($"MCP server config with ID {serverId} not found");

        if (existing.IsBuiltIn)
            throw new InvalidOperationException("Built-in MCP servers cannot be deleted.");

        servers.Remove(existing);
        await _repository.SaveAllAsync(servers);
    }

    public async Task<McpServerConfig> InstallFromLinkAsync(string link)
    {
        var serverConfig = McpInstallLinkParser.Parse(link);
        serverConfig.IsBuiltIn = false;
        serverConfig.BuiltInKey = null;
        await CreateAsync(serverConfig);
        return serverConfig;
    }

    private async Task<List<McpServerConfig>> LoadExternalServersAsync()
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        var changed = RemoveLegacyBuiltInServers(servers);
        if (changed)
            await _repository.SaveAllAsync(servers);

        return servers;
    }

    private static bool RemoveLegacyBuiltInServers(List<McpServerConfig> servers)
    {
        return servers.RemoveAll(IsBuiltInServer) > 0;
    }

    private static void ValidateExternalServerConfig(McpServerConfig serverConfig)
    {
        if (string.IsNullOrWhiteSpace(serverConfig.Name))
            throw new InvalidOperationException("Server name is required.");

        if (string.IsNullOrWhiteSpace(serverConfig.Command) && string.IsNullOrWhiteSpace(serverConfig.Sse))
            throw new InvalidOperationException("Either Command or Sse must be specified.");
    }

    private static bool IsBuiltInServer(McpServerConfig serverConfig)
    {
        return serverConfig.IsBuiltIn ||
               BuiltInMcpServerCatalog.IsBuiltInId(serverConfig.Id) ||
               BuiltInMcpServerCatalog.TryGetDefinition(serverConfig.BuiltInKey, out _);
    }
}

