using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IMcpServerConfigService
{
    Task<List<McpServerConfig>> GetAllServersAsync();
    Task<McpServerConfig?> GetServerByIdAsync(Guid id);
    Task<McpServerConfig> CreateServerAsync(McpServerConfig server);
    Task<McpServerConfig> UpdateServerAsync(McpServerConfig server);
    Task DeleteServerAsync(Guid id);
}
