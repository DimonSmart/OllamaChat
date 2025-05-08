using ChatClient.Api.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace ChatClient.Api.Services;

public class McpClientService : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpClientService> _logger;
    private IMcpClient? _mcpClient;
    public McpClientService(
        IConfiguration configuration,
        ILogger<McpClientService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IMcpClient?> CreateMcpClientAsync()
    {
        if (_mcpClient != null)
        {
            return _mcpClient;
        }

        var mcpServerConfigs = _configuration.GetSection("McpServers").Get<List<McpServerConfig>>() ?? [];

        if (mcpServerConfigs.Count == 0)
        {
            _logger.LogWarning("No MCP server configurations found");
            return null;
        }

        // For now, just connect to the first MCP server
        var config = mcpServerConfigs[0];
        _logger.LogInformation("Creating MCP client for server: {ServerName}", config.Name);

        if (string.IsNullOrEmpty(config.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty");
        }

        _mcpClient = await McpClientFactory.CreateAsync(
            clientTransport: new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Arguments ?? []
            }),
            clientOptions: null
        );

        _logger.LogInformation("MCP client created successfully for server: {ServerName}", config.Name);
        return _mcpClient;
    }

    public async Task<IReadOnlyList<McpClientTool>> GetMcpTools(IMcpClient mcpClient)
    {
        try
        {
            var tools = await mcpClient.ListToolsAsync();
            _logger.LogInformation("Retrieved {Count} tools from MCP server", tools.Count);
            return tools.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve MCP tools");
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
