using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System;
using System.Reflection;

namespace ChatClient.Api.Services;

public class McpClientService(
    IMcpServerConfigService mcpServerConfigService,
    McpSamplingService mcpSamplingService,
    IMcpUserInteractionService mcpUserInteractionService,
    ILogger<McpClientService> logger,
    ILoggerFactory loggerFactory) : IMcpClientService
{
    private List<McpClient>? _mcpClients = null;

    public async Task<IReadOnlyCollection<McpClient>> GetMcpClientsAsync(CancellationToken cancellationToken = default)
    {
        if (_mcpClients != null)
            return _mcpClients;
        _mcpClients = [];

        var mcpServerConfigs = await mcpServerConfigService.GetAllAsync();

        if (mcpServerConfigs.Count == 0)
        {
            logger.LogWarning("No MCP server configurations found");
            return _mcpClients;
        }

        foreach (var serverConfig in mcpServerConfigs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(serverConfig.Name))
            {
                logger.LogWarning("MCP server name is null or empty");
                continue;
            }

            logger.LogInformation("Creating MCP client for server: {ServerName}", serverConfig.Name);

            if (!string.IsNullOrWhiteSpace(serverConfig.Command))
                _mcpClients.Add(await CreateLocalMcpClientAsync(serverConfig, cancellationToken));
            if (!string.IsNullOrWhiteSpace(serverConfig.Sse))
                await AddSseClient(serverConfig, cancellationToken);

            logger.LogInformation("MCP client created successfully for server: {ServerName}", serverConfig.Name);
        }
        return _mcpClients;
    }

    private async Task AddSseClient(McpServerConfig serverConfig, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var httpTransport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = serverConfig.Name,
                    Endpoint = new Uri(serverConfig.Sse!),
                    TransportMode = HttpTransportMode.Sse
                },
                loggerFactory);
            var clientOptions = CreateClientOptions(serverConfig);
            var client = await McpClient.CreateAsync(httpTransport, clientOptions, loggerFactory, cancellationToken);
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

    private async Task<McpClient> CreateLocalMcpClientAsync(McpServerConfig serverConfig, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(serverConfig.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty for local connection");
        }

        // Use the application's executable directory as working directory instead of Environment.CurrentDirectory
        // This prevents MCP processes from accidentally changing the main application's working directory
        var applicationDirectory = AppContext.BaseDirectory;
        var clientOptions = CreateClientOptions(serverConfig);

        return await McpClient.CreateAsync(
            clientTransport: new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = serverConfig.Name,
                    Command = serverConfig.Command,
                    Arguments = serverConfig.Arguments ?? [],
                    WorkingDirectory = applicationDirectory // Use fixed application directory
                },
                loggerFactory),
            clientOptions: clientOptions,
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<McpClientTool>> GetMcpTools(McpClient mcpClient, CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            logger.LogInformation("Retrieved {Count} tools from MCP server", tools.Count);
            return tools.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve MCP tools");
        }

        return [];
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


    /// <summary>
    /// Creates client options that declare sampling/elicitation capabilities and register handlers.
    /// </summary>
    private McpClientOptions CreateClientOptions(McpServerConfig serverConfig)
    {
        return new McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "OllamaChat",
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
            },
            Capabilities = new ClientCapabilities
            {
                Sampling = new SamplingCapability
                {
                    Context = new SamplingContextCapability(),
                    Tools = new SamplingToolsCapability()
                },
                Elicitation = new ElicitationCapability()
            },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (request, cancellationToken) =>
                {
                    try
                    {
                        if (request == null)
                        {
                            throw new ArgumentNullException(nameof(request), "Elicitation request cannot be null");
                        }

                        logger.LogInformation(
                            "Handling elicitation request from server: {ServerName}. Mode: {Mode}",
                            serverConfig?.Name ?? "Unknown",
                            request.Mode ?? "form");

                        var result = await mcpUserInteractionService.HandleElicitationAsync(
                            serverConfig?.Name ?? "Unknown",
                            request,
                            cancellationToken);

                        logger.LogInformation(
                            "Elicitation request completed for server: {ServerName}",
                            serverConfig?.Name ?? "Unknown");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to handle elicitation request from server {ServerName}: {Message}",
                            serverConfig?.Name ?? "Unknown",
                            ex.Message);
                        throw;
                    }
                },
                SamplingHandler = async (request, progress, cancellationToken) =>
                {
                    try
                    {
                        if (request == null)
                        {
                            throw new ArgumentNullException(nameof(request), "Sampling request cannot be null");
                        }

                        logger.LogInformation("Handling sampling request with {MessageCount} messages from server: {ServerName}",
                            request.Messages?.Count ?? 0, serverConfig?.Name ?? "Unknown");

                        var result = await mcpSamplingService.HandleSamplingRequestAsync(request, progress, cancellationToken, serverConfig, serverConfig?.Id ?? Guid.Empty);

                        logger.LogInformation("Sampling request completed successfully for server: {ServerName}", serverConfig?.Name ?? "Unknown");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to handle sampling request from server {ServerName}: {Message}",
                            serverConfig?.Name ?? "Unknown", ex.Message);
                        throw;
                    }
                }
            }
        };
    }
}
