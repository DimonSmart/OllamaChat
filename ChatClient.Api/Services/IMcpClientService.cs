using ChatClient.Domain.Models;

using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public interface IMcpClientService : IAsyncDisposable
{
    Task<IReadOnlyCollection<IMcpClient>> GetMcpClientsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient, CancellationToken cancellationToken = default);
}
