using ChatClient.Application.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Reflection;

namespace ChatClient.Api.Services;

public class McpClientService(
    IMcpServerConfigService mcpServerConfigService,
    McpSamplingService mcpSamplingService,
    IMcpUserInteractionService mcpUserInteractionService,
    ILogger<McpClientService> logger,
    ILoggerFactory loggerFactory) : IMcpClientService
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private List<McpClient>? _mcpClients = null;
    private string? _configFingerprint = null;

    public async Task<IReadOnlyCollection<McpClient>> GetMcpClientsAsync(CancellationToken cancellationToken = default)
    {
        var mcpServerDescriptors = (await mcpServerConfigService.GetAllAsync())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fingerprint = BuildFingerprint(mcpServerDescriptors);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (_mcpClients != null && string.Equals(_configFingerprint, fingerprint, StringComparison.Ordinal))
                return _mcpClients;

            if (_mcpClients != null)
            {
                await DisposeClientsAsync(_mcpClients);
                _mcpClients = null;
            }

            var newClients = new List<McpClient>();

            if (mcpServerDescriptors.Count == 0)
            {
                logger.LogWarning("No MCP server configurations found");
                _mcpClients = newClients;
                _configFingerprint = fingerprint;
                return _mcpClients;
            }

            foreach (var serverDescriptor in mcpServerDescriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(serverDescriptor.Name))
                {
                    logger.LogWarning("MCP server name is null or empty");
                    continue;
                }

                logger.LogInformation("Creating MCP client for server: {ServerName}", serverDescriptor.Name);

                try
                {
                    if (serverDescriptor is IBuiltInMcpServerDescriptor builtInDefinition)
                    {
                        var builtInClient = await CreateBuiltInMcpClientAsync(builtInDefinition, cancellationToken);
                        newClients.Add(builtInClient);
                    }
                    else if (serverDescriptor is McpServerConfig serverConfig && !string.IsNullOrWhiteSpace(serverConfig.Command))
                    {
                        newClients.Add(await CreateLocalMcpClientAsync(serverConfig, cancellationToken));
                    }
                    else if (serverDescriptor is McpServerConfig sseServerConfig && !string.IsNullOrWhiteSpace(sseServerConfig.Sse))
                    {
                        await AddSseClient(newClients, sseServerConfig, cancellationToken);
                    }
                    else if (serverDescriptor is McpServerConfig unsupportedServerConfig)
                    {
                        logger.LogWarning(
                            "Skipping MCP server {ServerName} because neither Command nor Sse is configured.",
                            unsupportedServerConfig.Name);
                        continue;
                    }
                    else
                    {
                        logger.LogWarning(
                            "Skipping MCP server {ServerName} because server type is unsupported: {ServerType}",
                            serverDescriptor.Name,
                            serverDescriptor.GetType().FullName ?? "Unknown");
                        continue;
                    }

                    logger.LogInformation("MCP client created successfully for server: {ServerName}", serverDescriptor.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create MCP client for server: {ServerName}", serverDescriptor.Name);
                }
            }

            _mcpClients = newClients;
            _configFingerprint = fingerprint;
            return _mcpClients;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task AddSseClient(List<McpClient> clients, McpServerConfig serverConfig, CancellationToken cancellationToken)
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
            var clientOptions = CreateClientOptions(serverConfig, serverConfig);
            var client = await McpClient.CreateAsync(httpTransport, clientOptions, loggerFactory, cancellationToken);
            if (client != null)
            {
                clients.Add(client);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add network client for server: {ServerName}", serverConfig.Name);
        }
    }

    private async Task<McpClient> CreateBuiltInMcpClientAsync(IBuiltInMcpServerDescriptor definition, CancellationToken cancellationToken)
    {
        var (command, arguments) = GetBuiltInLaunchCommand(definition.Key);
        var applicationDirectory = AppContext.BaseDirectory;
        var clientOptions = CreateClientOptions(definition);

        return await McpClient.CreateAsync(
            clientTransport: new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = definition.Name,
                    Command = command,
                    Arguments = arguments,
                    WorkingDirectory = applicationDirectory
                },
                loggerFactory),
            clientOptions: clientOptions,
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);
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
        var clientOptions = CreateClientOptions(serverConfig, serverConfig);

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
        await _syncLock.WaitAsync();
        try
        {
            if (_mcpClients != null)
            {
                await DisposeClientsAsync(_mcpClients);
                _mcpClients = null;
            }

            _configFingerprint = null;
        }
        finally
        {
            _syncLock.Release();
        }

        GC.SuppressFinalize(this);
    }

    private async Task DisposeClientsAsync(IEnumerable<McpClient> clients)
    {
        foreach (var mcpClient in clients)
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
    }

    private static (string Command, string[] Arguments) GetBuiltInLaunchCommand(string builtInKey)
    {
        var processPath = Environment.ProcessPath;
        var processFileName = Path.GetFileName(processPath);
        var isDotnetHost = string.Equals(processFileName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(processFileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(processPath) && !isDotnetHost)
        {
            return (processPath, ["--mcp-builtin", builtInKey]);
        }

        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new InvalidOperationException("Unable to determine application assembly path for built-in MCP server launch.");

        var command = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath;
        return (command, [assemblyPath, "--mcp-builtin", builtInKey]);
    }

    private static string BuildFingerprint(IEnumerable<IMcpServerDescriptor> serverDescriptors)
    {
        return string.Join(
            "||",
            serverDescriptors
                .OrderBy(s => s.Id)
                .Select(s =>
                    s switch
                    {
                        IBuiltInMcpServerDescriptor builtIn =>
                            $"{builtIn.Id}|{builtIn.Name}|built-in|{builtIn.Key}",
                        McpServerConfig external =>
                            $"{external.Id}|{external.Name}|external|{external.Command}|{external.Sse}|{external.UpdatedAt:O}",
                        _ => $"{s.Id}|{s.Name}|unknown"
                    }));
    }


    /// <summary>
    /// Creates client options that declare sampling/elicitation capabilities and register handlers.
    /// </summary>
    private McpClientOptions CreateClientOptions(IMcpServerDescriptor serverDescriptor, McpServerConfig? serverConfig = null)
    {
        var serverName = string.IsNullOrWhiteSpace(serverDescriptor.Name) ? "Unknown" : serverDescriptor.Name;
        var serverId = serverDescriptor.Id ?? Guid.Empty;

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
                            serverName,
                            request.Mode ?? "form");

                        var result = await mcpUserInteractionService.HandleElicitationAsync(
                            serverName,
                            request,
                            cancellationToken);

                        logger.LogInformation(
                            "Elicitation request completed for server: {ServerName}",
                            serverName);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to handle elicitation request from server {ServerName}: {Message}",
                            serverName,
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
                            request.Messages?.Count ?? 0, serverName);

                        var result = await mcpSamplingService.HandleSamplingRequestAsync(
                            request,
                            progress,
                            cancellationToken,
                            serverConfig,
                            serverId);

                        logger.LogInformation("Sampling request completed successfully for server: {ServerName}", serverName);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to handle sampling request from server {ServerName}: {Message}",
                            serverName, ex.Message);
                        throw;
                    }
                }
            }
        };
    }
}
