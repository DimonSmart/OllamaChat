using ChatClient.Domain.Models;

using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public interface IMcpClientService : IAsyncDisposable
{
    Task<IReadOnlyCollection<McpClientHandle>> GetMcpClientsAsync(
        McpClientRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpClientTool>> GetMcpTools(McpClient mcpClient, CancellationToken cancellationToken = default);
}
