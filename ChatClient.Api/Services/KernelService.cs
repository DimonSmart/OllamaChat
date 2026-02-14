using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace ChatClient.Api.Services;

public class KernelService(
    McpFunctionIndexService indexService,
    IMcpClientService mcpClientService,
    ILogger<KernelService> logger)
{
    private readonly McpFunctionIndexService _indexService = indexService;
    private readonly IMcpClientService _mcpClientService = mcpClientService;

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

    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync(CancellationToken cancellationToken = default)
    {
        List<FunctionInfo> functions = [];

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
