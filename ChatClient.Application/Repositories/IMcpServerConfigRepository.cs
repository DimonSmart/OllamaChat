namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IMcpServerConfigRepository
{
    Task<List<McpServerConfig>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<McpServerConfig> servers, CancellationToken cancellationToken = default);
}

