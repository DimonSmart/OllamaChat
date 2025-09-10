namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IMcpServerConfigRepository
{
    Task<IReadOnlyCollection<McpServerConfig>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAllAsync(List<McpServerConfig> servers, CancellationToken cancellationToken = default);
}

