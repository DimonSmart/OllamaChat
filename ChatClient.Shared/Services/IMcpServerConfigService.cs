using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IMcpServerConfigService
{
    Task<List<McpServerConfig>> GetAllAsync();
    Task<McpServerConfig?> GetByIdAsync(Guid id);
    Task<McpServerConfig> CreateAsync(McpServerConfig server);
    Task<McpServerConfig> UpdateAsync(McpServerConfig server);
    Task DeleteAsync(Guid id);
}
