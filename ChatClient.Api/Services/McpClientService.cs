using ChatClient.Application.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Services;

public class McpClientService(
    IMcpServerConfigService mcpServerConfigService,
    McpSamplingService mcpSamplingService,
    IMcpUserInteractionService mcpUserInteractionService,
    ILogger<McpClientService> logger,
    ILoggerFactory loggerFactory) : IMcpClientService
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Dictionary<string, CachedClientSet> _clientSets = new(StringComparer.Ordinal);
    private const int MaxCachedClientSets = 12;

    private sealed class CachedClientSet(List<McpClientHandle> clients)
    {
        public List<McpClientHandle> Clients { get; } = clients;

        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    }

    public async Task<IReadOnlyCollection<McpClientHandle>> GetMcpClientsAsync(
        McpClientRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var mcpServerDescriptors = (await mcpServerConfigService.GetAllAsync())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedContext = requestContext ?? McpClientRequestContext.Empty;
        var fingerprint = BuildFingerprint(mcpServerDescriptors, normalizedContext);

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (_clientSets.TryGetValue(fingerprint, out var cachedClientSet))
            {
                cachedClientSet.LastAccessUtc = DateTime.UtcNow;
                return cachedClientSet.Clients;
            }

            var newClients = new List<McpClientHandle>();

            if (mcpServerDescriptors.Count == 0)
            {
                logger.LogWarning("No MCP server configurations found");
                _clientSets[fingerprint] = new CachedClientSet(newClients);
                return newClients;
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
                    var bindings = normalizedContext.FindBindingsFor(serverDescriptor);
                    if (bindings.Count == 0)
                    {
                        if (normalizedContext.HasBindings)
                        {
                            logger.LogDebug(
                                "Skipping unbound MCP server {ServerName} because request context already specifies explicit bindings.",
                                serverDescriptor.Name);
                            continue;
                        }

                        var defaultHandle = await CreateClientHandleAsync(serverDescriptor, null, cancellationToken);
                        if (defaultHandle is not null)
                        {
                            newClients.Add(defaultHandle);
                        }
                    }
                    else
                    {
                        foreach (var binding in bindings)
                        {
                            var boundHandle = await CreateClientHandleAsync(serverDescriptor, binding, cancellationToken);
                            if (boundHandle is not null)
                            {
                                newClients.Add(boundHandle);
                            }
                        }
                    }

                    logger.LogInformation("MCP client created successfully for server: {ServerName}", serverDescriptor.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create MCP client for server: {ServerName}", serverDescriptor.Name);
                }
            }

            _clientSets[fingerprint] = new CachedClientSet(newClients);
            await EvictIfNeededAsync(fingerprint);
            return newClients;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<McpClientHandle?> CreateClientHandleAsync(
        IMcpServerDescriptor serverDescriptor,
        McpServerSessionBinding? binding,
        CancellationToken cancellationToken)
    {
        if (serverDescriptor is IBuiltInMcpServerDescriptor builtInDefinition)
        {
            return await CreateBuiltInMcpClientAsync(
                builtInDefinition,
                binding,
                cancellationToken);
        }

        if (serverDescriptor is McpServerConfig serverConfig && !string.IsNullOrWhiteSpace(serverConfig.Command))
        {
            return await CreateLocalMcpClientAsync(serverConfig, binding, cancellationToken);
        }

        if (serverDescriptor is McpServerConfig sseServerConfig && !string.IsNullOrWhiteSpace(sseServerConfig.Sse))
        {
            return await CreateSseClientAsync(sseServerConfig, binding, cancellationToken);
        }

        if (serverDescriptor is McpServerConfig unsupportedServerConfig)
        {
            logger.LogWarning(
                "Skipping MCP server {ServerName} because neither Command nor Sse is configured.",
                unsupportedServerConfig.Name);
            return null;
        }

        logger.LogWarning(
            "Skipping MCP server {ServerName} because server type is unsupported: {ServerType}",
            serverDescriptor.Name,
            serverDescriptor.GetType().FullName ?? "Unknown");
        return null;
    }

    private async Task<McpClientHandle?> CreateSseClientAsync(
        McpServerConfig serverConfig,
        McpServerSessionBinding? binding,
        CancellationToken cancellationToken)
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
                return new McpClientHandle(client, serverConfig, binding?.Clone());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add network client for server: {ServerName}", serverConfig.Name);
        }

        return null;
    }

    private async Task<McpClientHandle> CreateBuiltInMcpClientAsync(
        IBuiltInMcpServerDescriptor definition,
        McpServerSessionBinding? binding,
        CancellationToken cancellationToken)
    {
        var (command, arguments) = GetBuiltInLaunchCommand(definition.Key, binding);
        var applicationDirectory = AppContext.BaseDirectory;
        var clientOptions = CreateClientOptions(definition);

        var client = await McpClient.CreateAsync(
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

        return new McpClientHandle(client, definition, binding?.Clone());
    }

    private async Task<McpClientHandle> CreateLocalMcpClientAsync(
        McpServerConfig serverConfig,
        McpServerSessionBinding? binding,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(serverConfig.Command))
        {
            throw new InvalidOperationException("MCP server command cannot be null or empty for local connection");
        }

        // Use the application's executable directory as working directory instead of Environment.CurrentDirectory
        // This prevents MCP processes from accidentally changing the main application's working directory
        var applicationDirectory = AppContext.BaseDirectory;
        var clientOptions = CreateClientOptions(serverConfig, serverConfig);

        var client = await McpClient.CreateAsync(
            clientTransport: new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = serverConfig.Name,
                    Command = serverConfig.Command,
                    Arguments = McpSessionBindingTransport.AppendArguments(serverConfig.Arguments, binding),
                    WorkingDirectory = applicationDirectory // Use fixed application directory
                },
                loggerFactory),
            clientOptions: clientOptions,
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);

        return new McpClientHandle(client, serverConfig, binding?.Clone());
    }

    public async Task<IReadOnlyList<McpClientTool>> GetMcpTools(McpClient mcpClient, CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            var serverName = string.IsNullOrWhiteSpace(mcpClient.ServerInfo.Name) ? "Unknown" : mcpClient.ServerInfo.Name;
            logger.LogDebug("Retrieved {Count} tools from MCP server {ServerName}", tools.Count, serverName);
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
            foreach (var clientSet in _clientSets.Values)
            {
                await DisposeClientsAsync(clientSet.Clients);
            }

            _clientSets.Clear();
        }
        finally
        {
            _syncLock.Release();
        }

        GC.SuppressFinalize(this);
    }

    private async Task DisposeClientsAsync(IEnumerable<McpClientHandle> clients)
    {
        foreach (var mcpClient in clients.Select(static client => client.Client).Distinct())
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

    private static (string Command, string[] Arguments) GetBuiltInLaunchCommand(
        string builtInKey,
        McpServerSessionBinding? binding)
    {
        var processPath = Environment.ProcessPath;
        var processFileName = Path.GetFileName(processPath);
        var isDotnetHost = string.Equals(processFileName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(processFileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(processPath) && !isDotnetHost)
        {
            return (processPath, McpSessionBindingTransport.AppendArguments(["--mcp-builtin", builtInKey], binding));
        }

        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new InvalidOperationException("Unable to determine application assembly path for built-in MCP server launch.");

        var command = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath;
        return (command, McpSessionBindingTransport.AppendArguments([assemblyPath, "--mcp-builtin", builtInKey], binding));
    }

    private static string BuildFingerprint(
        IEnumerable<IMcpServerDescriptor> serverDescriptors,
        McpClientRequestContext requestContext)
    {
        var payload = string.Join(
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
                    })
                .Append($"ctx:{requestContext.BuildFingerprint()}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task EvictIfNeededAsync(string currentFingerprint)
    {
        while (_clientSets.Count > MaxCachedClientSets)
        {
            var evicted = _clientSets
                .Where(entry => !string.Equals(entry.Key, currentFingerprint, StringComparison.Ordinal))
                .OrderBy(entry => entry.Value.LastAccessUtc)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(evicted.Key))
            {
                break;
            }

            _clientSets.Remove(evicted.Key);
            await DisposeClientsAsync(evicted.Value.Clients);
        }
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
