using ChatClient.Shared.Models;

using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public interface IMcpClientService : IAsyncDisposable
{
    Task<IReadOnlyCollection<IMcpClient>> GetMcpClientsAsync();
    Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient);
}
