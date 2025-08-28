using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using System.Linq;

namespace ChatClient.Api.Services;

public class KernelService(
    McpFunctionIndexService indexService,
    ILogger<KernelService> logger)
{
    private readonly McpFunctionIndexService _indexService = indexService;
    private IMcpClientService? _mcpClientService;

    public void SetMcpClientService(IMcpClientService mcpClientService)
    {
        _mcpClientService = mcpClientService;
    }

    public async Task<IReadOnlyCollection<string>> GetFunctionsToRegisterAsync(
        FunctionSettings functionSettings,
        string? userQuery,
        CancellationToken cancellationToken = default)
    {
        if (functionSettings.AutoSelectCount > 0 && !string.IsNullOrWhiteSpace(userQuery))
        {
            return await _indexService.SelectRelevantFunctionsAsync(userQuery, functionSettings.AutoSelectCount, cancellationToken);
        }

        if (functionSettings.SelectedFunctions.Any())
        {
            return functionSettings.SelectedFunctions;
        }

        return [];
    }

    /// <summary>
    /// Public method to register MCP tools for agent architecture
    /// </summary>
    public async Task RegisterMcpToolsPublicAsync(
        Kernel kernel,
        IEnumerable<string> functionNames,
        CancellationToken cancellationToken = default)
    {
        if (_mcpClientService == null)
        {
            logger.LogWarning("MCP client service not available for registering tools");
            return;
        }

        try
        {
            var mcpClients = await _mcpClientService.GetMcpClientsAsync(cancellationToken);
            if (mcpClients.Count == 0)
            {
                logger.LogWarning("MCP client could not be created");
                return;
            }

            foreach (var mcpClient in mcpClients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mcpTools = await _mcpClientService.GetMcpTools(mcpClient, cancellationToken);
                if (mcpTools.Count == 0)
                {
                    logger.LogWarning($"No MCP tools available to register for server: {mcpClient.ServerInfo.Name} ");
                    continue;
                }

                var toolsToRegister = mcpTools
                    .Where(t => functionNames.Contains($"{mcpClient.ServerInfo.Name}:{t.Name}"))
                    .ToList();
                if (toolsToRegister.Count == 0)
                {
                    logger.LogWarning($"No MCP tools matched the requested function names. In mcp server: {mcpClient.ServerInfo.Name}");
                    continue;
                }
                var pluginFunctions = toolsToRegister.Select(tool => tool.AsKernelFunction()).ToList();
                var pluginName = mcpClient.ServerInfo.Name ?? "McpServer";
                kernel.Plugins.AddFromFunctions(pluginName, pluginFunctions);
                logger.LogInformation("Registered {Count} MCP tools for server {Server}", pluginFunctions.Count, pluginName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register MCP tools: {Message}", ex.Message);
        }
    }

    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var functions = new List<FunctionInfo>();

        if (_mcpClientService == null)
        {
            logger.LogWarning("MCP client service not available for getting functions");
            return functions;
        }

        try
        {
            var mcpClients = await _mcpClientService.GetMcpClientsAsync(cancellationToken);
            if (mcpClients.Count == 0)
                return [];
            foreach (var mcpClient in mcpClients)
            {
                var mcpTools = await _mcpClientService.GetMcpTools(mcpClient, cancellationToken);
                var toolFuncs = mcpTools.Select(tool =>
                    new FunctionInfo
                    {
                        Name = $"{mcpClient.ServerInfo.Name}:{tool.Name}",
                        ServerName = mcpClient.ServerInfo.Name ?? string.Empty,
                        DisplayName = tool.Name,
                        Description = tool.Description
                    });
                functions.AddRange(toolFuncs);
            }

            return functions;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get available functions: {Message}", ex.Message);
            return functions;
        }
    }
}
