using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IMcpServerConfigService
{
    Task<List<McpServerConfig>> GetAllAsync();
    Task<McpServerConfig?> GetByIdAsync(Guid id);
    Task CreateAsync(McpServerConfig serverConfig);
    Task UpdateAsync(McpServerConfig serverConfig);
    Task DeleteAsync(Guid id);
}
