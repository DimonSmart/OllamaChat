using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;
using System.Reflection;

namespace ChatClient.Api.Services;

public class McpClientService(
    IMcpServerConfigService mcpServerConfigService,
    ILogger<McpClientService> logger) : IAsyncDisposable
{
    private List<IMcpClient>? _mcpClients = null;

    public async Task<IReadOnlyCollection<IMcpClient>> GetMcpClientsAsync()
    {
        if (_mcpClients != null) return _mcpClients;
        _mcpClients = [];

        var mcpServerConfigs = await mcpServerConfigService.GetAllServersAsync();

        if (mcpServerConfigs.Count == 0)
        {
            logger.LogWarning("No MCP server configurations found");
            return _mcpClients;
        }

        foreach (var serverConfig in mcpServerConfigs)
        {
            if (string.IsNullOrWhiteSpace(serverConfig.Name))
            {
                logger.LogWarning("MCP server name is null or empty");
                continue;
            }

            logger.LogInformation("Creating MCP client for server: {ServerName}", serverConfig.Name);

            if (!string.IsNullOrWhiteSpace(serverConfig.Command)) _mcpClients.Add(await CreateLocalMcpClientAsync(serverConfig));
            if (!string.IsNullOrWhiteSpace(serverConfig.Sse)) await AddSseClient(serverConfig);

            logger.LogInformation("MCP client created successfully for server: {ServerName}", serverConfig.Name);
        }
        return _mcpClients;
    }

    private async Task AddSseClient(McpServerConfig serverConfig)
    {
        try
        {
            var httpTransport = new SseClientTransport(new SseClientTransportOptions { Endpoint = new Uri(serverConfig.Sse!) });
            var client = await McpClientFactory.CreateAsync(httpTransport);
            if (_mcpClients != null && client != null)
            {
                _mcpClients.Add(client);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add network client for server: {ServerName}", serverConfig.Name);
        }
    }

    private static async Task<IMcpClient> CreateLocalMcpClientAsync(McpServerConfig config)
    {
        if (string.IsNullOrEmpty(config.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty for local connection");
        }
        // Use the application's executable directory as working directory instead of Environment.CurrentDirectory
        // This prevents MCP processes from accidentally changing the main application's working directory
        var applicationDirectory = AppContext.BaseDirectory;

        return await McpClientFactory.CreateAsync(
            clientTransport: new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Arguments ?? [],
                WorkingDirectory = applicationDirectory // Use fixed application directory
            }),
            clientOptions: null
        );
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
        if (_mcpClients == null)
        {
            return;
        }

        foreach (var mcpClient in _mcpClients)
        {
            try
            {
                await mcpClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispose MCP client");
            }
        }

        _mcpClients = null;
        GC.SuppressFinalize(this);
    }
}
