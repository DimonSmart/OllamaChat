using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IMcpServerConfigService
{
    Task<IReadOnlyCollection<IMcpServerDescriptor>> GetAllAsync();
    Task<IMcpServerDescriptor?> GetByIdAsync(Guid serverId);
    Task CreateAsync(McpServerConfig serverConfig);
    Task UpdateAsync(McpServerConfig serverConfig);
    Task DeleteAsync(Guid serverId);
    Task<McpServerConfig> InstallFromLinkAsync(string link);
}
