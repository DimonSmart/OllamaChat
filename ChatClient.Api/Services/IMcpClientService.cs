using ModelContextProtocol.Client;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public interface IMcpClientService : IAsyncDisposable
{
    Task<IReadOnlyCollection<IMcpClient>> GetMcpClientsAsync();
    Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient);
}
