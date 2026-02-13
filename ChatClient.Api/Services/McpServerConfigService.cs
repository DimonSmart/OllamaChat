using ChatClient.Application.Helpers;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class McpServerConfigService(IMcpServerConfigRepository repository) : IMcpServerConfigService
{
    private readonly IMcpServerConfigRepository _repository = repository;

    public async Task<IReadOnlyCollection<McpServerConfig>> GetAllAsync()
    {
        var servers = await LoadServersAsync(ensureBuiltIns: true, persistBuiltInChanges: true);
        return servers;
    }

    public async Task<McpServerConfig?> GetByIdAsync(Guid serverId)
    {
        var servers = await LoadServersAsync(ensureBuiltIns: true, persistBuiltInChanges: true);
        return servers.FirstOrDefault(s => s.Id == serverId);
    }

    public async Task CreateAsync(McpServerConfig serverConfig)
    {
        if (serverConfig.IsBuiltIn || BuiltInMcpServerCatalog.TryGetDefinition(serverConfig.BuiltInKey, out _))
            throw new InvalidOperationException("Built-in MCP servers are managed by the application and cannot be created manually.");

        var servers = await LoadServersAsync(ensureBuiltIns: true, persistBuiltInChanges: true);

        ValidateExternalServerConfig(serverConfig);

        serverConfig.Id ??= Guid.NewGuid();
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
        var servers = await LoadServersAsync(ensureBuiltIns: true, persistBuiltInChanges: true);
        var index = servers.FindIndex(s => s.Id == serverConfig.Id);
        if (index == -1)
            throw new KeyNotFoundException($"MCP server config with ID {serverConfig.Id} not found");

        var existing = servers[index];
        if (existing.IsBuiltIn)
        {
            existing.IsEnabled = serverConfig.IsEnabled;
            existing.UpdatedAt = DateTime.UtcNow;
            servers[index] = existing;
            await _repository.SaveAllAsync(servers);
            return;
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
        var servers = await LoadServersAsync(ensureBuiltIns: true, persistBuiltInChanges: true);
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
        serverConfig.IsEnabled = true;
        serverConfig.IsBuiltIn = false;
        serverConfig.BuiltInKey = null;
        await CreateAsync(serverConfig);
        return serverConfig;
    }

    private async Task<List<McpServerConfig>> LoadServersAsync(bool ensureBuiltIns, bool persistBuiltInChanges)
    {
        var servers = (await _repository.GetAllAsync()).ToList();

        if (!ensureBuiltIns)
            return servers;

        var nowUtc = DateTime.UtcNow;
        var changed = EnsureBuiltInServers(servers, nowUtc);
        if (changed && persistBuiltInChanges)
        {
            await _repository.SaveAllAsync(servers);
        }

        return servers;
    }

    private static bool EnsureBuiltInServers(List<McpServerConfig> servers, DateTime nowUtc)
    {
        var changed = false;

        foreach (var definition in BuiltInMcpServerCatalog.Definitions)
        {
            var existing = servers.FirstOrDefault(s => s.Id == definition.Id);
            existing ??= servers.FirstOrDefault(s =>
                s.IsBuiltIn &&
                string.Equals(s.BuiltInKey, definition.Key, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                servers.Add(BuiltInMcpServerCatalog.CreateConfig(definition, nowUtc));
                changed = true;
                continue;
            }

            if (existing.Id != definition.Id)
            {
                existing.Id = definition.Id;
                changed = true;
            }

            if (!string.Equals(existing.Name, definition.Name, StringComparison.Ordinal))
            {
                existing.Name = definition.Name;
                changed = true;
            }

            if (!existing.IsBuiltIn)
            {
                existing.IsBuiltIn = true;
                changed = true;
            }

            if (!string.Equals(existing.BuiltInKey, definition.Key, StringComparison.Ordinal))
            {
                existing.BuiltInKey = definition.Key;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(existing.Command) ||
                !string.IsNullOrWhiteSpace(existing.Sse) ||
                (existing.Arguments is { Length: > 0 }))
            {
                existing.Command = null;
                existing.Sse = null;
                existing.Arguments = null;
                changed = true;
            }

            if (existing.CreatedAt == default)
            {
                existing.CreatedAt = nowUtc;
                changed = true;
            }
        }

        return changed;
    }

    private static void ValidateExternalServerConfig(McpServerConfig serverConfig)
    {
        if (string.IsNullOrWhiteSpace(serverConfig.Name))
            throw new InvalidOperationException("Server name is required.");

        if (string.IsNullOrWhiteSpace(serverConfig.Command) && string.IsNullOrWhiteSpace(serverConfig.Sse))
            throw new InvalidOperationException("Either Command or Sse must be specified.");
    }
}

