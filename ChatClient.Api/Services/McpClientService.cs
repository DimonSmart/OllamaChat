using ChatClient.Api.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace ChatClient.Api.Services;

public class McpClientService(
    IConfiguration configuration,
    ILogger<McpClientService> logger) : IAsyncDisposable
{
    private IMcpClient? _mcpClient;

    public async Task<IMcpClient?> CreateMcpClientAsync()
    {
        if (_mcpClient != null)
        {
            return _mcpClient;
        }

        var mcpServerConfigs = configuration.GetSection("McpServers").Get<List<McpServerConfig>>() ?? [];

        if (mcpServerConfigs.Count == 0)
        {
            logger.LogWarning("No MCP server configurations found");
            return null;
        }

        // For now, just connect to the first MCP server
        var config = mcpServerConfigs[0];
        logger.LogInformation("Creating MCP client for server: {ServerName}", config.Name);

        if (!string.IsNullOrWhiteSpace(config.Command)) await CreateLocalMcpClientAsync(config);
        if (!string.IsNullOrWhiteSpace(config.Url)) await CreateNetworkMcpClientAsync(config);

        logger.LogInformation("MCP client created successfully for server: {ServerName}", config.Name);
        return _mcpClient;
    }

    private async Task<IMcpClient> CreateLocalMcpClientAsync(McpServerConfig config)
    {
        if (string.IsNullOrEmpty(config.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty for local connection");
        }

        return await McpClientFactory.CreateAsync(
            clientTransport: new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Arguments ?? []
            }),
            clientOptions: null
        );
    }

    private async Task<IMcpClient> CreateNetworkMcpClientAsync(McpServerConfig config)
    {
        if (string.IsNullOrEmpty(config.Url))
        {
            throw new InvalidOperationException("Host cannot be null or empty for network connection");
        }

        var httpTransport = new SseClientTransport(
           new SseClientTransportOptions
           {
               Endpoint = new Uri(config.Url)
           }
       );

        return await McpClientFactory.CreateAsync(httpTransport);
    }

    public async Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient)
    {
        try
        {
            var tools = await mcpClient.ListToolsAsync();
            logger.LogInformation("Retrieved {Count} tools from MCP server", tools.Count);
            return tools.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve MCP tools");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient == null)
        {
            return;
        }

        await _mcpClient.DisposeAsync();
        _mcpClient = null;
        GC.SuppressFinalize(this);
    }
}
