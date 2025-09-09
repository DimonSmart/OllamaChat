using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IMcpServerConfigService
{
    Task<List<McpServerConfig>> GetAllAsync();
    Task<McpServerConfig?> GetByIdAsync(Guid id);
    Task CreateAsync(McpServerConfig serverConfig);
    Task UpdateAsync(McpServerConfig serverConfig);
    Task DeleteAsync(Guid id);
}
