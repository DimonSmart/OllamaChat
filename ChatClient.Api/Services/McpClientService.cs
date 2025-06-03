using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Reflection;

namespace ChatClient.Api.Services;

public class McpClientService(
    IMcpServerConfigService mcpServerConfigService,
    McpSamplingService mcpSamplingService,
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
            
            // Create client options with sampling capabilities
            var clientOptions = this.CreateClientOptions();
            
            var client = await McpClientFactory.CreateAsync(httpTransport, clientOptions);
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
    
    private async Task<IMcpClient> CreateLocalMcpClientAsync(McpServerConfig config)
    {
        if (string.IsNullOrEmpty(config.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty for local connection");
        }        
        // Use the application's executable directory as working directory instead of Environment.CurrentDirectory
        // This prevents MCP processes from accidentally changing the main application's working directory
        var applicationDirectory = AppContext.BaseDirectory;

        // Create client options with sampling capabilities
        var clientOptions = CreateClientOptions();

        return await McpClientFactory.CreateAsync(
            clientTransport: new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Arguments ?? [],
                WorkingDirectory = applicationDirectory // Use fixed application directory
            }),
            clientOptions: clientOptions
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
    }    /// <summary>
    /// Creates client options that declare sampling capabilities and register the sampling handler
    /// </summary>
    private McpClientOptions CreateClientOptions()
    {
        return new McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "OllamaChat",
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
            },
            Capabilities = new ClientCapabilities
            {                Sampling = new SamplingCapability
                {
                    SamplingHandler = HandleSamplingRequestAsync
                }
            }
        };
    }    /// <summary>
    /// Handles sampling requests from MCP servers by delegating to the sampling service
    /// </summary>
    private async ValueTask<CreateMessageResult> HandleSamplingRequestAsync(
        CreateMessageRequestParams? request, 
        IProgress<ProgressNotificationValue> progress, 
        CancellationToken cancellationToken)
    {
        try
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            logger.LogInformation("Handling sampling request with {MessageCount} messages", 
                request.Messages?.Count ?? 0);

            // Delegate to the sampling service
            var result = await mcpSamplingService.HandleSamplingRequestAsync(request, progress, cancellationToken);
            
            logger.LogInformation("Sampling request completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle sampling request: {Message}", ex.Message);
            throw;
        }
    }
}
